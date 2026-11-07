using System.Runtime.CompilerServices;

// Task 7.1: the CLI is `internal`, and its behavior (stderr warnings, exit codes,
// load-file creation) is part of the spec — so the test assembly exercises `Main`
// directly rather than re-implementing it.
[assembly: InternalsVisibleTo("DedupTool.Tests")]
