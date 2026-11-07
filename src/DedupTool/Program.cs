using System.Text.Json;
using DedupTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: DedupTool <batch.json> <loadfile.csv>");
            return 2;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        Batch batch;
        try
        {
            batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(args[0]), options)
                    ?? throw new JsonException("file contains no JSON object");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unusable input fails the run (task 7.2, input-handling spec): one clear
            // error line on stderr, exit 1 (distinct from the usage contract's 2), and
            // no load file — the writer is never reached. Nothing is thrown at the
            // user as a stack trace, and an existing file at the output path is left
            // untouched rather than truncated by a failed run.
            Console.Error.WriteLine($"error: cannot read batch \"{args[0]}\": {ex.Message}");
            return 1;
        }

        var result = Deduper.Run(batch);

        // Warnings first (task 7.1, design D5): one line per skipped document, in input
        // order, on stderr. They never affect the exit code — invalid documents are a
        // best-effort condition (input-handling spec), not a run failure.
        foreach (var warning in result.Warnings)
        {
            var doc = string.IsNullOrEmpty(warning.DocId) ? "(missing docId)" : warning.DocId;
            Console.Error.WriteLine($"warning: {doc}: {warning.Reason}");
        }

        LoadFileWriter.Write(args[1], result.Rows);

        Console.WriteLine($"{batch.BatchId}: {batch.Documents.Count} documents in, {result.Rows.Count} out, {result.Suppressed} suppressed as duplicates.");
        return 0;
    }
}
