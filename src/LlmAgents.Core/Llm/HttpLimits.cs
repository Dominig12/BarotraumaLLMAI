using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

static class HttpLimits
{
    /// <summary>A model reply is a few kilobytes; a broken proxy or a wrong URL can stream megabytes of HTML.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    public static async Task<string> ReadCappedAsync(this HttpContent content, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxResponseBytes)
                throw new InvalidDataException($"response larger than {MaxResponseBytes / (1024 * 1024)} MB: is the provider URL right?");
            buffer.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
