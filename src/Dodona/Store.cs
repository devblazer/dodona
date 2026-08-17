using Microsoft.Data.Sqlite;

namespace Dodona;

/// <summary>
/// The registry (design §12). One writer — the daemon — behind one connection and one
/// lock. WAL + synchronous=FULL. Every state change is one transaction: claim-check and
/// claim-insert atomically (§6), token grant and land atomically (§7). pane_events
/// dedupes shim redelivery via UNIQUE(lane_id, seq).
/// </summary>
sealed class Store : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        Migrate();
    }

    void Migrate()
    {
        long v;
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = "PRAGMA user_version;"; v = (long)c.ExecuteScalar()!; }
        if (v < 1)
        {
            Exec("""
                CREATE TABLE lanes(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'alive',
                    pipe_name TEXT NOT NULL DEFAULT '',
                    session_id TEXT,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE pane_events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lane_id INTEGER NOT NULL,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    body TEXT NOT NULL,
                    seq INTEGER,
                    raw TEXT,
                    UNIQUE(lane_id, seq)
                );
                CREATE TABLE events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    lane_id INTEGER,
                    detail TEXT
                );
                PRAGMA user_version = 1;
                """);
        }
        if (v < 2)
        {
            Exec("""
                CREATE TABLE tickets(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lane_id INTEGER,
                    title TEXT NOT NULL,
                    branch TEXT NOT NULL DEFAULT '',
                    worktree TEXT NOT NULL DEFAULT '',
                    state TEXT NOT NULL DEFAULT 'open',            -- open | landed | abandoned
                    merge_mode TEXT NOT NULL DEFAULT 'on-approval',
                    approved INTEGER NOT NULL DEFAULT 0,
                    created_ts TEXT NOT NULL,
                    landed_ts TEXT
                );
                CREATE TABLE claims(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ticket_id INTEGER NOT NULL,
                    kind TEXT NOT NULL,                            -- path | newfile | subtree | symbol
                    value TEXT NOT NULL,
                    created_ts TEXT NOT NULL
                );
                CREATE TABLE merge_token(
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    holder_ticket INTEGER,
                    generation INTEGER NOT NULL DEFAULT 0,
                    granted_ts TEXT,
                    expires_ts TEXT,
                    main_sha TEXT
                );
                INSERT INTO merge_token(id, generation) VALUES (1, 0);
                CREATE TABLE token_queue(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ticket_id INTEGER NOT NULL UNIQUE,
                    enqueued_ts TEXT NOT NULL
                );
                PRAGMA user_version = 2;
                """);
        }
        if (v < 3)
        {
            Exec("""
                ALTER TABLE lanes ADD COLUMN presence TEXT NOT NULL DEFAULT '';
                ALTER TABLE lanes ADD COLUMN role TEXT NOT NULL DEFAULT 'work';   -- work | router | dispatcher
                CREATE TABLE routing_decisions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    input TEXT NOT NULL,
                    tier TEXT NOT NULL,                 -- prefix | focus | classifier
                    target_lane INTEGER,                -- the classifier's opinion
                    delivered_lane INTEGER,             -- where it actually went (final)
                    confidence TEXT,
                    retargeted INTEGER NOT NULL DEFAULT 0,
                    undone INTEGER NOT NULL DEFAULT 0   -- the undo keystroke is labeled data (§4)
                );
                CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 3;
                """);
        }
        if (v < 4)
        {
            // M3: the decision feed persists until acked (§8) — acked is state, not
            // deletion; the pane replay must still show the row.
            Exec("""
                ALTER TABLE pane_events ADD COLUMN acked INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 4;
                """);
        }
        if (v < 5)
        {
            // M4: a swap that cannot be seamless is a decision with three answers, and
            // "when it lands" must survive a daemon restart — so it is a row (§14).
            Exec("""
                CREATE TABLE swaps(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    requested_ts TEXT NOT NULL,
                    exe TEXT NOT NULL,
                    build TEXT NOT NULL,
                    schema_version INTEGER NOT NULL,
                    shim_protocol INTEGER NOT NULL,
                    blocker TEXT,                       -- NULL once nothing is in the way
                    mode TEXT NOT NULL,                 -- ask | now | when-it-lands | hold
                    state TEXT NOT NULL,                -- pending | armed | held | swapped | failed | superseded
                    decided_ts TEXT,
                    note TEXT
                );
                PRAGMA user_version = 5;
                """);
        }
        if (v < 6)
        {
            // A workspace holds several repositories. The merge token becomes per
            // repository: a ticket landing in `engine` must not queue behind one landing
            // in `tools` — different mains, no possible conflict, no reason to serialize.
            // Existing single-repo stores migrate to the repo named ".", the workspace
            // root itself, so nothing about them changes.
            Exec("""
                ALTER TABLE tickets ADD COLUMN repo TEXT NOT NULL DEFAULT '.';
                ALTER TABLE token_queue ADD COLUMN repo TEXT NOT NULL DEFAULT '.';
                CREATE TABLE merge_token_v6(
                    repo TEXT PRIMARY KEY,
                    holder_ticket INTEGER,
                    generation INTEGER NOT NULL DEFAULT 0,
                    granted_ts TEXT,
                    expires_ts TEXT,
                    main_sha TEXT
                );
                INSERT INTO merge_token_v6(repo, holder_ticket, generation, granted_ts, expires_ts, main_sha)
                    SELECT '.', holder_ticket, generation, granted_ts, expires_ts, main_sha FROM merge_token WHERE id = 1;
                DROP TABLE merge_token;
                ALTER TABLE merge_token_v6 RENAME TO merge_token;
                PRAGMA user_version = 6;
                """);
        }
    }

    static string Now() => DateTime.UtcNow.ToString("o");

    void Exec(string sql)
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }

    // ------------------------------------------------------------- events & lanes

    public void Event(string kind, long? laneId, string? detail)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO events(ts, kind, lane_id, detail) VALUES ($ts, $k, $l, $d);";
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$l", (object?)laneId ?? DBNull.Value);
            c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    public long LaneCreate(string title)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO lanes(title, created_ts) VALUES ($t, $ts); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$ts", Now());
            return (long)c.ExecuteScalar()!;
        }
    }

    public void LanePipe(long id, string pipe) => Set("UPDATE lanes SET pipe_name = $v WHERE id = $id;", id, pipe);
    public void LaneState(long id, string state) => Set("UPDATE lanes SET state = $v WHERE id = $id;", id, state);
    public void LaneSession(long id, string session) => Set("UPDATE lanes SET session_id = $v WHERE id = $id;", id, session);
    public void LanePresence(long id, string presence) => Set("UPDATE lanes SET presence = $v WHERE id = $id;", id, presence);
    public void LaneRole(long id, string role) => Set("UPDATE lanes SET role = $v WHERE id = $id;", id, role);

    public void KvSet(string key, string value)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO kv(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    public string? KvGet(string key)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT value FROM kv WHERE key = $k;";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public long RoutingInsert(string input, string tier, long? target, long? delivered, string? confidence)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                INSERT INTO routing_decisions(ts, input, tier, target_lane, delivered_lane, confidence)
                VALUES ($ts, $i, $t, $tl, $dl, $c); SELECT last_insert_rowid();
                """;
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$i", input);
            c.Parameters.AddWithValue("$t", tier);
            c.Parameters.AddWithValue("$tl", (object?)target ?? DBNull.Value);
            c.Parameters.AddWithValue("$dl", (object?)delivered ?? DBNull.Value);
            c.Parameters.AddWithValue("$c", (object?)confidence ?? DBNull.Value);
            return (long)c.ExecuteScalar()!;
        }
    }

    public void RoutingRetarget(long id, long targetLane, string confidence)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE routing_decisions SET tier = 'classifier', target_lane = $t, delivered_lane = $t, confidence = $c, retargeted = 1 WHERE id = $id;";
            c.Parameters.AddWithValue("$t", targetLane);
            c.Parameters.AddWithValue("$c", confidence);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    void Set(string sql, long id, string value)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$v", value);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public record LaneRow(long Id, string Title, string State, string Pipe, string? Session, string Presence, string Role);

    public List<LaneRow> LanesAll()
    {
        lock (_lock)
        {
            var list = new List<LaneRow>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, title, state, pipe_name, session_id, presence, role FROM lanes ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new LaneRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                     r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)));
            return list;
        }
    }

    public bool PaneEvent(long laneId, string kind, string body, long? seq, string? raw)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO pane_events(lane_id, ts, kind, body, seq, raw) VALUES ($l, $ts, $k, $b, $s, $r);";
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$b", body);
            c.Parameters.AddWithValue("$s", (object?)seq ?? DBNull.Value);
            c.Parameters.AddWithValue("$r", (object?)raw ?? DBNull.Value);
            return c.ExecuteNonQuery() > 0;
        }
    }

    public bool PaneAck(long paneEventId)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE pane_events SET acked = 1 WHERE id = $id AND kind = 'announcement' AND acked = 0;";
            c.Parameters.AddWithValue("$id", paneEventId);
            return c.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Mark a routing decision undone (§4: the undo keystroke is labeled data)
    /// and return where it was delivered so the daemon can send a retraction.</summary>
    public (long? DeliveredLane, string Input)? RoutingUndo(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                UPDATE routing_decisions SET undone = 1 WHERE id = $id AND undone = 0
                RETURNING delivered_lane, input;
                """;
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return (r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1));
        }
    }

    public List<string> Tail(long laneId, int n)
    {
        lock (_lock)
        {
            var rows = new List<string>();
            using var c = _db.CreateCommand();
            c.CommandText = """
                SELECT ts, kind, body FROM (
                    SELECT id, ts, kind, body FROM pane_events WHERE lane_id = $l ORDER BY id DESC LIMIT $n
                ) ORDER BY id;
                """;
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add($"{r.GetString(0)}  {r.GetString(1),-10}  {r.GetString(2)}");
            return rows;
        }
    }

    // ------------------------------------------------------------- tickets & claims

    public record TicketRow(long Id, long? LaneId, string Title, string Branch, string Worktree,
                            string State, string MergeMode, bool Approved, string Repo);

    /// <summary>Check-and-insert in ONE transaction (§6, §12): claims are intersected
    /// against every open ticket's claims; no overlap → ticket + claims inserted;
    /// overlap → nothing inserted, conflicts returned.</summary>
    public (long Id, List<string> Conflicts) TicketCreate(long? laneId, string title, string mode, string repo,
                                                          List<(string Kind, string Value)> claims)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var conflicts = FindConflicts(tx, claims, excludeTicket: null);
            if (conflicts.Count > 0) { tx.Rollback(); return (-1, conflicts); }

            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO tickets(lane_id, title, merge_mode, repo, created_ts) VALUES ($l, $t, $m, $r, $ts); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$l", (object?)laneId ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$r", repo);
            c.Parameters.AddWithValue("$ts", Now());
            var id = (long)c.ExecuteScalar()!;
            InsertClaims(tx, id, claims);
            tx.Commit();
            return (id, conflicts);
        }
    }

    public List<string> ClaimExtend(long ticketId, List<(string Kind, string Value)> claims)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var conflicts = FindConflicts(tx, claims, excludeTicket: ticketId);
            if (conflicts.Count > 0) { tx.Rollback(); return conflicts; }
            InsertClaims(tx, ticketId, claims);
            tx.Commit();
            return conflicts;
        }
    }

    List<string> FindConflicts(SqliteTransaction tx, List<(string Kind, string Value)> claims, long? excludeTicket)
    {
        var conflicts = new List<string>();
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = """
            SELECT cl.kind, cl.value, t.id, t.title FROM claims cl
            JOIN tickets t ON t.id = cl.ticket_id
            WHERE t.state = 'open' AND ($x IS NULL OR t.id != $x);
            """;
        c.Parameters.AddWithValue("$x", (object?)excludeTicket ?? DBNull.Value);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var (ek, ev, tid, ttitle) = (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3));
            foreach (var (k, v) in claims)
                if (Claims.Overlap(k, v, ek, ev))
                    conflicts.Add($"{k}:{v} overlaps {ek}:{ev} held by ticket {tid} ({ttitle})");
        }
        return conflicts;
    }

    void InsertClaims(SqliteTransaction tx, long ticketId, List<(string Kind, string Value)> claims)
    {
        foreach (var (k, v) in claims)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO claims(ticket_id, kind, value, created_ts) VALUES ($t, $k, $v, $ts);";
            c.Parameters.AddWithValue("$t", ticketId);
            c.Parameters.AddWithValue("$k", k);
            c.Parameters.AddWithValue("$v", v);
            c.Parameters.AddWithValue("$ts", Now());
            c.ExecuteNonQuery();
        }
    }

    public void TicketSetLane(long id, long laneId)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE tickets SET lane_id = $l WHERE id = $id;";
            c.Parameters.AddWithValue("$l", laneId);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void TicketSetGit(long id, string branch, string worktree)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE tickets SET branch = $b, worktree = $w WHERE id = $id;";
            c.Parameters.AddWithValue("$b", branch);
            c.Parameters.AddWithValue("$w", worktree);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void TicketState(long id, string state) => Set("UPDATE tickets SET state = $v WHERE id = $id;", id, state);
    public void TicketApprove(long id) => Set("UPDATE tickets SET approved = 1 WHERE id = $id AND $v = $v;", id, "1");

    const string TicketCols = "id, lane_id, title, branch, worktree, state, merge_mode, approved, repo";

    static TicketRow ReadTicket(SqliteDataReader r) =>
        new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1, r.GetString(8));

    public TicketRow? Ticket(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {TicketCols} FROM tickets WHERE id = $id;";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            return r.Read() ? ReadTicket(r) : null;
        }
    }

    public List<TicketRow> Tickets()
    {
        lock (_lock)
        {
            var list = new List<TicketRow>();
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {TicketCols} FROM tickets ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(ReadTicket(r));
            return list;
        }
    }

    public List<(string Kind, string Value)> TicketClaims(long id)
    {
        lock (_lock)
        {
            var list = new List<(string, string)>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT kind, value FROM claims WHERE ticket_id = $id;";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    // ------------------------------------------------------------- merge token (§7)

    public record TokenRow(string Repo, long? Holder, long Generation, string? ExpiresTs, string? MainSha);

    public TokenRow TokenRead(string repo)
    {
        lock (_lock) { return ReadToken(null, repo); }
    }

    /// <summary>Every repository's token — what `token-status` shows in a workspace.</summary>
    public List<TokenRow> TokensAll()
    {
        lock (_lock)
        {
            var list = new List<TokenRow>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT repo, holder_ticket, generation, expires_ts, main_sha FROM merge_token ORDER BY repo;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TokenRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2),
                                      r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    TokenRow ReadToken(SqliteTransaction? tx, string repo)
    {
        // Rows appear on first use: a repository that has never been landed in has no
        // token row, which is indistinguishable from a free one.
        using (var ins = _db.CreateCommand())
        {
            if (tx is not null) ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO merge_token(repo, generation) VALUES ($r, 0);";
            ins.Parameters.AddWithValue("$r", repo);
            ins.ExecuteNonQuery();
        }
        using var c = _db.CreateCommand();
        if (tx is not null) c.Transaction = tx;
        c.CommandText = "SELECT holder_ticket, generation, expires_ts, main_sha FROM merge_token WHERE repo = $r;";
        c.Parameters.AddWithValue("$r", repo);
        using var r2 = c.ExecuteReader();
        r2.Read();
        return new TokenRow(repo, r2.IsDBNull(0) ? null : r2.GetInt64(0), r2.GetInt64(1),
                            r2.IsDBNull(2) ? null : r2.GetString(2), r2.IsDBNull(3) ? null : r2.GetString(3));
    }

    static bool Expired(TokenRow t) => t.ExpiresTs is not null && DateTime.Parse(t.ExpiresTs).ToUniversalTime() < DateTime.UtcNow;

    /// <summary>Request the merge token. Lease + FIFO in one transaction. An expired
    /// holder is reclaimed here — a crashed holder cannot wedge the queue (§7, §12).</summary>
    public (string Status, long Generation, int Position) TokenRequest(long ticketId, string repo, int leaseSec, Func<string> mainSha)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);

            if (t.Holder is not null && Expired(t))
            {
                TxSet(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE repo = $r;", repo);
                TxEvent(tx, "token_expired_reclaimed", $"{repo}: was ticket {t.Holder}");
                t = t with { Holder = null };
            }

            if (t.Holder == ticketId)
            {
                tx.Commit();
                return ("granted", t.Generation, 0);
            }

            // ensure enqueued — the queue is per repository, like the token
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT OR IGNORE INTO token_queue(ticket_id, repo, enqueued_ts) VALUES ($t, $r, $ts);";
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$r", repo);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }

            long head;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT ticket_id FROM token_queue WHERE repo = $r ORDER BY id LIMIT 1;";
                c.Parameters.AddWithValue("$r", repo);
                head = (long)c.ExecuteScalar()!;
            }

            if (t.Holder is null && head == ticketId)
            {
                var sha = mainSha();
                using var c = _db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = """
                    DELETE FROM token_queue WHERE ticket_id = $t;
                    UPDATE merge_token SET holder_ticket = $t, generation = generation + 1,
                        granted_ts = $ts, expires_ts = $exp, main_sha = $sha WHERE repo = $r;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$r", repo);
                c.Parameters.AddWithValue("$ts", Now());
                c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
                c.Parameters.AddWithValue("$sha", sha);
                c.ExecuteNonQuery();
                var gen = ReadToken(tx, repo).Generation;
                TxEvent(tx, "token_granted", $"ticket {ticketId} repo {repo} gen {gen} main {sha[..8]} lease {leaseSec}s");
                tx.Commit();
                return ("granted", gen, 0);
            }

            int pos;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = """
                    SELECT COUNT(*) FROM token_queue
                    WHERE repo = $r AND id <= (SELECT id FROM token_queue WHERE ticket_id = $t);
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$r", repo);
                pos = Convert.ToInt32(c.ExecuteScalar());
            }
            TxEvent(tx, "token_queued", $"ticket {ticketId} repo {repo} position {pos}");
            tx.Commit();
            return ("queued", t.Generation, pos);
        }
    }

    public bool TokenRenew(long ticketId, string repo, int leaseSec)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder != ticketId || Expired(t)) { tx.Rollback(); return false; }
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE merge_token SET expires_ts = $exp WHERE repo = $r;";
            c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
            c.Parameters.AddWithValue("$r", repo);
            c.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public void TokenRelease(long ticketId, string repo)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder == ticketId)
            {
                TxSet(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE repo = $r;", repo);
                TxEvent(tx, "token_released", $"ticket {ticketId} repo {repo}");
            }
            tx.Commit();
        }
    }

    /// <summary>The land fence + commit, one transaction (§7): holder identity and lease
    /// re-checked HERE, in the same transaction that records the land and frees the
    /// claims. Returns false if the fence refuses.</summary>
    public bool LandCommit(long ticketId, string repo, out string reason)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx, repo);
            if (t.Holder != ticketId) { reason = $"token holder for {repo} is {(t.Holder?.ToString() ?? "nobody")}, not ticket {ticketId}"; tx.Rollback(); return false; }
            if (Expired(t)) { reason = "lease expired"; tx.Rollback(); return false; }

            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = """
                    UPDATE tickets SET state = 'landed', landed_ts = $ts WHERE id = $t;
                    DELETE FROM claims WHERE ticket_id = $t;
                    DELETE FROM token_queue WHERE ticket_id = $t;
                    UPDATE merge_token SET holder_ticket = NULL WHERE repo = $r;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$r", repo);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }
            TxEvent(tx, "landed", $"ticket {ticketId} repo {repo} gen {t.Generation}; claims released");
            tx.Commit();
            reason = "";
            return true;
        }
    }

    // ------------------------------------------------------------- swaps (§13/§14)

    public record SwapRow(long Id, string Exe, string Build, int Schema, int ShimProtocol,
                          string? Blocker, string Mode, string State);

    public long SwapCreate(string exe, string build, int schema, int shimProto, string? blocker, string mode, string state)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            // One live proposal at a time: a newer build supersedes an older parked one.
            TxExec(tx, "UPDATE swaps SET state = 'superseded' WHERE state IN ('pending','armed','held');");
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = """
                INSERT INTO swaps(requested_ts, exe, build, schema_version, shim_protocol, blocker, mode, state)
                VALUES ($ts, $e, $b, $sc, $sp, $bl, $m, $st); SELECT last_insert_rowid();
                """;
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$e", exe);
            c.Parameters.AddWithValue("$b", build);
            c.Parameters.AddWithValue("$sc", schema);
            c.Parameters.AddWithValue("$sp", shimProto);
            c.Parameters.AddWithValue("$bl", (object?)blocker ?? DBNull.Value);
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$st", state);
            var id = (long)c.ExecuteScalar()!;
            tx.Commit();
            return id;
        }
    }

    public SwapRow? SwapLive()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                SELECT id, exe, build, schema_version, shim_protocol, blocker, mode, state
                FROM swaps WHERE state IN ('pending','armed','held') ORDER BY id DESC LIMIT 1;
                """;
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new SwapRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                               r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7));
        }
    }

    public void SwapSet(long id, string mode, string state, string? note = null)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE swaps SET mode = $m, state = $st, decided_ts = $ts, note = COALESCE($n, note) WHERE id = $id;";
            c.Parameters.AddWithValue("$m", mode);
            c.Parameters.AddWithValue("$st", state);
            c.Parameters.AddWithValue("$ts", Now());
            c.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public List<string> SwapsAll()
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, requested_ts, build, state, mode, COALESCE(blocker,'-') FROM swaps ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add($"swap {r.GetInt64(0)}  {r.GetString(1)}  build={r.GetString(2)}  state={r.GetString(3)}  mode={r.GetString(4)}  blocker={r.GetString(5)}");
            return list;
        }
    }

    void TxExec(SqliteTransaction tx, string sql)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    void TxSet(SqliteTransaction tx, string sql, string repo)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.Parameters.AddWithValue("$r", repo);
        c.ExecuteNonQuery();
    }

    void TxEvent(SqliteTransaction tx, string kind, string detail)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT INTO events(ts, kind, lane_id, detail) VALUES ($ts, $k, NULL, $d);";
        c.Parameters.AddWithValue("$ts", Now());
        c.Parameters.AddWithValue("$k", kind);
        c.Parameters.AddWithValue("$d", detail);
        c.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
