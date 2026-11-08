using System;
using System.IO;

namespace Proteus.Services;

/// <summary>How an output texture write ended (#91).</summary>
internal enum WriteOutcome
{
    /// <summary>Written and in place.</summary>
    Written,
    /// <summary>Failed for a reason a retry would not fix (bad input, encoder error, missing folder).</summary>
    Failed,
    /// <summary>The file could not be put in place — held past the move's retries. Worth a retry composite.</summary>
    OutputWriteFailed,
}

/// <summary>
/// An output file Proteus writes (a shell or skin texture, a shell material or model) could not be written or moved into
/// place for a transient reason — the game or a sync tool holding the destination, a sharing violation — after the
/// write's own bounded retries (#91). Only these count as a failed publish worth retrying: a missing source, a bad path or
/// a malformed input is not transient and never thrown as this.
/// </summary>
internal sealed class OutputWriteException(string path, Exception inner)
    : IOException($"could not write {path}: {inner.Message}", inner)
{
    /// <summary>The output file that could not be written.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// Whether a failure of an output write is worth retrying: access denied, or an I/O error other than a missing file
    /// or folder or a path too long (those are the same every time).
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        OutputWriteException => true,
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
        IOException => true,
        _ => false,
    };
}
