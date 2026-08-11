using System.Buffers;
using System.Text;

namespace Styloagent.Core.Transcripts;

/// <summary>
/// Shared tail reader for JSONL agent transcripts: yields the last lines of a file, newest first.
///
/// Decodes LAZILY, one line at a time, walking the raw tail buffer backwards. The obvious version — decode
/// the whole window to a string and String.Split it — allocated ~1.3 MB per call (a 256 KB byte buffer, a
/// ~512 KB UTF-16 string, then a fresh string per line) even though callers almost always match within the
/// first few lines from the end. The cockpit refreshes every agent's usage readout off this every ~3s, so
/// across a live fleet it was ~11% of ALL process allocation — feeding the background-GC churn that made the
/// cockpit sluggish.
///
/// Splitting on the '\n' BYTE is safe because UTF-8 is self-synchronising: continuation bytes are 0x80-0xBF
/// and can never collide with 0x0A. Each line is decoded from a whole-line slice, so multi-byte glyphs are
/// never cut in half.
/// </summary>
internal static class TranscriptTail
{
    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from the end of <paramref name="path"/> and yields its
    /// non-empty, trimmed lines newest-first. A leading fragment cut mid-line by the tail seek is skipped.
    /// </summary>
    public static IEnumerable<string> Lines(string path, int maxBytes)
    {
        long length;
        int take;
        byte[] buffer;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            length = fs.Length;
            take = (int)Math.Min(length, maxBytes);
            fs.Seek(length - take, SeekOrigin.Begin);
            buffer = ArrayPool<byte>.Shared.Rent(take);
            try
            {
                var read = 0;
                while (read < take)
                {
                    var n = fs.Read(buffer, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }
                take = read;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        // The pooled buffer may be LARGER than requested — `take` is the only valid length.
        try
        {
            var end = take;                      // exclusive end of the line being scanned
            for (var i = take - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                var line = Decode(buffer, i + 1, end - (i + 1));
                if (line.Length > 0) yield return line;
                end = i;
            }
            // The leading segment is a COMPLETE line only when the window covered the whole file; otherwise
            // it is a fragment cut mid-line by the tail seek and must be skipped.
            if (take == length && end > 0)
            {
                var first = Decode(buffer, 0, end);
                if (first.Length > 0) yield return first;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static string Decode(byte[] b, int offset, int count)
            => count <= 0 ? "" : Encoding.UTF8.GetString(b, offset, count).Trim();
    }
}
