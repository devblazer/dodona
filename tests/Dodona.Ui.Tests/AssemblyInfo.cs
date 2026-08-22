using Xunit;

// ══ NO PARALLEL COLLECTIONS IN THIS PROJECT ══
//
// xunit's default runs one test class per collection, in parallel. RecognizerContract's real
// subject is DeepgramRecognizer pointed at a closed loopback port, and the only way to point it
// there is three PROCESS-WIDE environment variables (DODONA_STT_ENDPOINT, DODONA_STT_TOKEN,
// DODONA_STT_CONNECT_MS -- the endpoint override exists precisely so a check can exercise the
// socket failure without egress). A sibling class running beside it would see them set, or see
// them restored halfway through its own run.
//
// The cost is nothing measurable: this project is a handful of pure facts with no window, no
// daemon and no store. The alternative -- reasoning about which test observes which environment
// -- is what kept the routing ladder's failure invisible for two days.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
