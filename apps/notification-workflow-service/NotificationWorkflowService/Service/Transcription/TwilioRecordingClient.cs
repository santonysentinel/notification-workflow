using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TwilioRecordingClient(IHttpClientFactory factory, TranscriptionOptions options, ILogger<TwilioRecordingClient>? logger = null) : ITwilioRecordingClient
{
    internal const string ClientName = "TranscriptionTwilio";
    private readonly ILogger<TwilioRecordingClient> diagnosticsLogger = logger ?? NullLogger<TwilioRecordingClient>.Instance;

    public async Task<TranscriptionAudio> OpenAsync(TranscriptionWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        if (work.RecordingId <= 0 || !string.Equals(work.Provider, "Twilio", StringComparison.OrdinalIgnoreCase) ||
            work.AccountSid != options.Twilio.AccountSid || !TranscriptionOptions.Sid(work.RecordingSid, "RE") ||
            work.RecordingStatus != "completed" || work.Channels is not (1 or 2) || work.DurationSeconds is null or <= 0 ||
            work.DurationSeconds > options.MaxDurationSeconds)
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_RECORDING");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.MediaHeaderTimeoutSeconds));
        var client = factory.CreateClient(ClientName);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.twilio.com/2010-04-01/Accounts/{work.AccountSid}/Recordings/{work.RecordingSid}.wav?RequestedChannels={work.Channels}");
            string user = options.Twilio.ApiKeySid.Length > 0 ? options.Twilio.ApiKeySid : options.Twilio.AccountSid;
            string secret = options.Twilio.ApiKeySid.Length > 0 ? options.Twilio.ApiKeySecret : options.Twilio.AuthToken;
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret)));
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                diagnosticsLogger.LogWarning("Transcription Twilio media retrieval returned HTTP {HttpStatus} for job {JobId}, recording {RecordingId}",
                    (int)response.StatusCode, work.JobId, work.RecordingId);
                bool retryable = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
                throw new TranscriptionException(retryable ? TranscriptionFailure.TransientPreparation : TranscriptionFailure.Validation,
                    response.StatusCode == HttpStatusCode.NotFound ? "MEDIA_MISSING" : response.StatusCode == HttpStatusCode.TooManyRequests ? "MEDIA_RATE_LIMIT" : "MEDIA_REJECTED", retryable);
            }
            if (response.Content.Headers.ContentEncoding.Count != 0 ||
                response.Content.Headers.ContentType?.MediaType is not ("audio/wav" or "audio/x-wav" or "audio/wave") ||
                response.Content.Headers.ContentLength > options.MaxAudioBytes)
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_MEDIA_RESPONSE");
            Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var header = await ReadWaveHeaderAsync(stream, work.Channels.Value, work.DurationSeconds.Value, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != header.Length)
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_MEDIA_LENGTH");
            return new TranscriptionAudio(new WaveStream(stream, header.Prefix, header.Length, options.MaxAudioBytes), header.Length,
                new ResponseOwner(response, client));
        }
        catch (TranscriptionException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Read recording media", exception, work.JobId, work.RecordingId);
            response?.Dispose(); client.Dispose(); throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { response?.Dispose(); client.Dispose(); throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Read recording media", exception, work.JobId, work.RecordingId,
                $"Media retrieval exceeded its {options.MediaHeaderTimeoutSeconds}-second deadline.");
            response?.Dispose(); client.Dispose(); throw new TranscriptionException(TranscriptionFailure.TransientPreparation, "MEDIA_TIMEOUT", true);
        }
        catch (InvalidDataException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Validate recording WAV", exception, work.JobId, work.RecordingId);
            response?.Dispose(); client.Dispose(); throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_WAV");
        }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Read recording media", exception, work.JobId, work.RecordingId);
            response?.Dispose(); client.Dispose(); throw new TranscriptionException(TranscriptionFailure.TransientPreparation, "MEDIA_UNAVAILABLE", true);
        }
    }

    private async Task<(byte[] Prefix, long Length)> ReadWaveHeaderAsync(Stream stream, int channels, int durationSeconds, CancellationToken cancellationToken)
    {
        using var prefix = new MemoryStream();
        async Task<byte[]> Read(int count)
        {
            if (count < 0 || prefix.Length + count > 65536) throw new InvalidDataException();
            byte[] bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            prefix.Write(bytes);
            return bytes;
        }
        byte[] riff = await Read(12).ConfigureAwait(false);
        if (!riff.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !riff.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException();
        long length = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4)) + 8L;
        if (length < 44 || length > options.MaxAudioBytes) throw new InvalidDataException();
        uint byteRate = 0;
        ushort blockAlign = 0;
        bool hasFormat = false;
        while (prefix.Length + 8 <= length)
        {
            byte[] chunk = await Read(8).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (prefix.Length + size + (size & 1) > length) throw new InvalidDataException();
            if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (!hasFormat || size == 0 || size % blockAlign != 0 || (double)size / byteRate > options.MaxDurationSeconds ||
                    Math.Abs((double)size / byteRate - durationSeconds) > 2) throw new InvalidDataException();
                return (prefix.ToArray(), length);
            }
            byte[] data = await Read(checked((int)(size + (size & 1)))).ConfigureAwait(false);
            if (!chunk.AsSpan(0, 4).SequenceEqual("fmt "u8)) continue;
            if (hasFormat || size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) != channels ||
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)) != 16) throw new InvalidDataException();
            uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            byteRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
            if (sampleRate is < 8000 or > 192000 || blockAlign != channels * 2 || byteRate != sampleRate * blockAlign) throw new InvalidDataException();
            hasFormat = true;
        }
        throw new InvalidDataException();
    }

    private sealed class ResponseOwner(HttpResponseMessage response, HttpClient client) : IDisposable
    {
        public void Dispose() { response.Dispose(); client.Dispose(); }
    }

    private sealed class WaveStream(Stream source, byte[] prefix, long expected, long maximum) : Stream
    {
        private int prefixOffset;
        private long total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expected;
        public override long Position { get => total; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            int count;
            if (prefixOffset < prefix.Length)
            {
                count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
                prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
                prefixOffset += count;
            }
            else count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            total += count;
            if (total > expected || total > maximum || (count == 0 && total != expected)) throw new IOException("WAV stream length mismatch.");
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await source.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}