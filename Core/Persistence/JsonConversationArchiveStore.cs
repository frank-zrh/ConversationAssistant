using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Persistence;

/// <summary>Stores versioned, human-readable UTF-8 JSON, without encryption.</summary>
public sealed class JsonConversationArchiveStore : IConversationArchiveStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _directory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonConversationArchiveStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string GetPath(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A conversation ID is required.", nameof(sessionId));
        return Path.Combine(_directory, $"{sessionId:N}.json");
    }

    public Task<string> SaveAsync(ConversationArchive archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        // Directory creation, durable flush and atomic replacement must never run on the capture/UI caller.
        return Task.Run(() => SaveCoreAsync(archive, cancellationToken), cancellationToken);
    }

    public Task<ConversationArchive> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => LoadCoreAsync(path, cancellationToken), cancellationToken);
    }

    private async Task<string> SaveCoreAsync(ConversationArchive archive, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? ownedStagingPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var destination = GetPath(archive.SessionId);
            var stagingPath = Path.Combine(_directory, $"{archive.SessionId:N}.{Guid.NewGuid():N}.tmp");
            await using (var file = new FileStream(stagingPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 64 * 1024
            }))
            {
                ownedStagingPath = stagingPath;
                using var bounded = new BoundedArchiveStream(file);
                await JsonSerializer.SerializeAsync(bounded, archive.Document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination))
                File.Replace(stagingPath, destination, destinationBackupFileName: null);
            else
                File.Move(stagingPath, destination);
            ownedStagingPath = null;
            return destination;
        }
        finally
        {
            if (ownedStagingPath is not null)
            {
                try { File.Delete(ownedStagingPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "Conversation archive temporary-file cleanup failed: {0}", error.GetType().Name);
                }
            }
            _writeGate.Release();
        }
    }

    private static async Task<ConversationArchive> LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 64 * 1024
        });
        if (file.Length > ConversationArchiveLimits.MaximumFileBytes)
            throw BoundedArchiveStream.SizeLimitExceeded();
        try
        {
            using var bounded = new BoundedArchiveStream(file);
            var document = await JsonSerializer.DeserializeAsync<ArchiveDocument>(bounded, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ConversationArchive.FromDocument(document
                ?? throw ConversationArchive.Invalid("The document is required."));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The conversation archive is not valid JSON or contains invalid field values.", exception);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = ConversationArchiveLimits.MaximumJsonDepth,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters =
        {
            new JsonStringEnumConverter<ConversationLanguage>(allowIntegerValues: false),
            new JsonStringEnumConverter<AnswerStyle>(allowIntegerValues: false),
            new JsonStringEnumConverter<QuestionStatus>(allowIntegerValues: false),
            new JsonStringEnumConverter<ConversationAnalysisTrigger>(allowIntegerValues: false)
        }
    };
}

/// <summary>A byte budget over a borrowed stream; disposing it does not dispose the underlying file.</summary>
internal sealed class BoundedArchiveStream(Stream stream) : Stream
{
    private long _processed;

    public override bool CanRead => stream.CanRead;
    public override bool CanWrite => stream.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = stream.Read(buffer, offset, ReadCount(count));
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await stream.ReadAsync(buffer[..ReadCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWriteFits(count);
        stream.Write(buffer, offset, count);
        _processed += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWriteFits(buffer.Length);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _processed += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int ReadCount(int requested) =>
        (int)Math.Min(requested, ConversationArchiveLimits.MaximumFileBytes - _processed + 1);

    private void Account(int count)
    {
        _processed += count;
        if (_processed > ConversationArchiveLimits.MaximumFileBytes) throw SizeLimitExceeded();
    }

    private void EnsureWriteFits(int count)
    {
        if (count > ConversationArchiveLimits.MaximumFileBytes - _processed) throw SizeLimitExceeded();
    }

    internal static InvalidDataException SizeLimitExceeded() =>
        new("The conversation archive exceeds the 64 MiB size limit.");
}
