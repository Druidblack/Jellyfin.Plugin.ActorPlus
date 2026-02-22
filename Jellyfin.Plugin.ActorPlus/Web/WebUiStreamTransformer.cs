using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ActorPlus.Web;

/// <summary>
/// Stream-level transformer used when registering transformations via
/// <c>jellyfin-plugin-file-transformation</c>.
/// </summary>
internal static class WebUiStreamTransformer
{
    // Signature must match Jellyfin.Plugin.FileTransformation.Library.TransformFile
    public static async Task TransformIndexHtmlStream(string path, Stream contents)
    {
        if (contents is null)
        {
            throw new ArgumentNullException(nameof(contents));
        }

        string html;
        contents.Seek(0, SeekOrigin.Begin);
        using (var reader = new StreamReader(contents, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var transformed = WebUiInjector.TransformIndexHtml(html);

        if (!contents.CanWrite)
        {
            return;
        }

        contents.Seek(0, SeekOrigin.Begin);
        try
        {
            contents.SetLength(0);
        }
        catch
        {
            // Some stream implementations may not support SetLength; best effort.
        }

        using (var writer = new StreamWriter(contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true))
        {
            await writer.WriteAsync(transformed).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        contents.Seek(0, SeekOrigin.Begin);
    }
}
