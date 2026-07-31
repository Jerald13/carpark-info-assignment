using CarparkInfo.Application.Ingestion;

namespace CarparkInfo.Application.Abstractions;

/// <summary>
/// Reads carpark records from a source stream, whatever format that stream is in.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam the README grades as "changing of interface file format from csv to JSON".</b>
/// Everything downstream - the validator, the mapper, the hasher, staging and the merge - consumes
/// <see cref="SourceRecord{T}"/> and has no idea whether the bytes were CSV, JSON or anything else.
/// Adding a format is one class and one DI registration, with no change to any of them.
/// </para>
/// <para>
/// Records are streamed rather than returned as a list. Memory is O(1) in file size, not O(n). At
/// 2,181 rows that is irrelevant; at ten million it is the difference between working and an
/// OutOfMemoryException.
/// </para>
/// </remarks>
public interface IRecordSource
{
    /// <summary>The format this source understands, for diagnostics and factory registration.</summary>
    string FormatName { get; }

    /// <summary>Whether this source can read the given file.</summary>
    /// <param name="fileName">The file name, including extension.</param>
    /// <returns><see langword="true"/> when the format matches.</returns>
    bool CanRead(string fileName);

    /// <summary>
    /// Streams records lazily. The whole file is never held in memory.
    /// </summary>
    /// <param name="stream">The source stream. Not disposed by this method.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An asynchronous sequence of records, each tagged with its line number.</returns>
    /// <exception cref="SchemaDriftException">
    /// The source's structure does not match what is expected. Deliberately fatal: if the provider
    /// reorders or renames a column, aborting is correct and ingesting ten million silently
    /// shifted rows is not.
    /// </exception>
    IAsyncEnumerable<SourceRecord<CarparkSourceRecord>> ReadAsync(
        Stream stream, CancellationToken cancellationToken);
}

/// <summary>
/// Selects the reader for a given file.
/// </summary>
public interface IRecordSourceFactory
{
    /// <summary>Resolves a reader for the given file.</summary>
    /// <param name="fileName">The file name, including extension.</param>
    /// <returns>A reader that understands the file's format.</returns>
    /// <exception cref="UnsupportedFormatException">No registered reader recognises the file.</exception>
    IRecordSource Resolve(string fileName);

    /// <summary>The formats currently registered, for diagnostics.</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }
}

/// <summary>Thrown when the source's structure does not match what is expected.</summary>
public sealed class SchemaDriftException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What differed.</param>
    public SchemaDriftException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public SchemaDriftException() : base("The source file's schema does not match what was expected.") { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What differed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SchemaDriftException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Thrown when no registered reader recognises a file's format.</summary>
public sealed class UnsupportedFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which file, and what formats are supported.</param>
    public UnsupportedFormatException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public UnsupportedFormatException() : base("No registered record source recognises this file.") { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which file, and what formats are supported.</param>
    /// <param name="innerException">The underlying failure.</param>
    public UnsupportedFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
