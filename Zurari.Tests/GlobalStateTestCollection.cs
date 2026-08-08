using Xunit;

// Tests that mutate the shared static Program.State (Panes/SplitActive/FocusedPane) must not
// run concurrently with each other — xUnit parallelizes different test classes by default, and
// Program.State is a single process-wide static field (see docs/superpowers/specs — the app is
// single-instance by design). Grouping these classes into one named collection serializes them
// against each other while leaving the rest of the suite free to parallelize normally.
[CollectionDefinition("GlobalStateTests", DisableParallelization = true)]
public class GlobalStateTestCollection { }
