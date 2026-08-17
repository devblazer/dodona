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
                            string State, string MergeMode, bool Approved);

    /// <summary>Check-and-insert in ONE transaction (§6, §12): claims are intersected
    /// against every open ticket's claims; no overlap → ticket + claims inserted;
    /// overlap → nothing inserted, conflicts returned.</summary>
    public (long Id, List<string> Conflicts) TicketCreate(long? laneId, string title, string mode,
                                                          List<(string Kind, string Value)> claims)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var conflicts = FindConflicts(tx, claims, excludeTicket: null);
            if (conflicts.Count > 0) { tx.Rollback(); return (-1, conflicts); }

            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO tickets(lane_id, title, merge_mode, created_ts) VALUES ($l, $t, $m, $ts); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$l", (object?)laneId ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$m", mode);
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

    public TicketRow? Ticket(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, lane_id, title, branch, worktree, state, merge_mode, approved FROM tickets WHERE id = $id;";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new TicketRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2),
                                 r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1);
        }
    }

    public List<TicketRow> Tickets()
    {
        lock (_lock)
        {
            var list = new List<TicketRow>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, lane_id, title, branch, worktree, state, merge_mode, approved FROM tickets ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TicketRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2),
                                       r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1));
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

    public record TokenRow(long? Holder, long Generation, string? ExpiresTs, string? MainSha);

    public TokenRow TokenRead()
    {
        lock (_lock) { return ReadToken(null); }
    }

    TokenRow ReadToken(SqliteTransaction? tx)
    {
        using var c = _db.CreateCommand();
        if (tx is not null) c.Transaction = tx;
        c.CommandText = "SELECT holder_ticket, generation, expires_ts, main_sha FROM merge_token WHERE id = 1;";
        using var r = c.ExecuteReader();
        r.Read();
        return new TokenRow(r.IsDBNull(0) ? null : r.GetInt64(0), r.GetInt64(1),
                            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    static bool Expired(TokenRow t) => t.ExpiresTs is not null && DateTime.Parse(t.ExpiresTs).ToUniversalTime() < DateTime.UtcNow;

    /// <summary>Request the merge token. Lease + FIFO in one transaction. An expired
    /// holder is reclaimed here — a crashed holder cannot wedge the queue (§7, §12).</summary>
    public (string Status, long Generation, int Position) TokenRequest(long ticketId, int leaseSec, Func<string> mainSha)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx);

            if (t.Holder is not null && Expired(t))
            {
                TxExec(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE id = 1;");
                TxEvent(tx, "token_expired_reclaimed", $"was ticket {t.Holder}");
                t = t with { Holder = null };
            }

            if (t.Holder == ticketId)
            {
                tx.Commit();
                return ("granted", t.Generation, 0);
            }

            // ensure enqueued
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT OR IGNORE INTO token_queue(ticket_id, enqueued_ts) VALUES ($t, $ts);";
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }

            long head;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT ticket_id FROM token_queue ORDER BY id LIMIT 1;";
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
                        granted_ts = $ts, expires_ts = $exp, main_sha = $sha WHERE id = 1;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$ts", Now());
                c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
                c.Parameters.AddWithValue("$sha", sha);
                c.ExecuteNonQuery();
                var gen = ReadToken(tx).Generation;
                TxEvent(tx, "token_granted", $"ticket {ticketId} gen {gen} main {sha[..8]} lease {leaseSec}s");
                tx.Commit();
                return ("granted", gen, 0);
            }

            int pos;
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT COUNT(*) FROM token_queue WHERE id <= (SELECT id FROM token_queue WHERE ticket_id = $t);";
                c.Parameters.AddWithValue("$t", ticketId);
                pos = Convert.ToInt32(c.ExecuteScalar());
            }
            TxEvent(tx, "token_queued", $"ticket {ticketId} position {pos}");
            tx.Commit();
            return ("queued", t.Generation, pos);
        }
    }

    public bool TokenRenew(long ticketId, int leaseSec)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx);
            if (t.Holder != ticketId || Expired(t)) { tx.Rollback(); return false; }
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE merge_token SET expires_ts = $exp WHERE id = 1;";
            c.Parameters.AddWithValue("$exp", DateTime.UtcNow.AddSeconds(leaseSec).ToString("o"));
            c.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public void TokenRelease(long ticketId)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx);
            if (t.Holder == ticketId)
            {
                TxExec(tx, "UPDATE merge_token SET holder_ticket = NULL WHERE id = 1;");
                TxEvent(tx, "token_released", $"ticket {ticketId}");
            }
            tx.Commit();
        }
    }

    /// <summary>The land fence + commit, one transaction (§7): holder identity and lease
    /// re-checked HERE, in the same transaction that records the land and frees the
    /// claims. Returns false if the fence refuses.</summary>
    public bool LandCommit(long ticketId, out string reason)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var t = ReadToken(tx);
            if (t.Holder != ticketId) { reason = $"token holder is {(t.Holder?.ToString() ?? "nobody")}, not ticket {ticketId}"; tx.Rollback(); return false; }
            if (Expired(t)) { reason = "lease expired"; tx.Rollback(); return false; }

            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = """
                    UPDATE tickets SET state = 'landed', landed_ts = $ts WHERE id = $t;
                    DELETE FROM claims WHERE ticket_id = $t;
                    DELETE FROM token_queue WHERE ticket_id = $t;
                    UPDATE merge_token SET holder_ticket = NULL WHERE id = 1;
                    """;
                c.Parameters.AddWithValue("$t", ticketId);
                c.Parameters.AddWithValue("$ts", Now());
                c.ExecuteNonQuery();
            }
            TxEvent(tx, "landed", $"ticket {ticketId} gen {t.Generation}; claims released");
            tx.Commit();
            reason = "";
            return true;
        }
    }

    void TxExec(SqliteTransaction tx, string sql)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
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
