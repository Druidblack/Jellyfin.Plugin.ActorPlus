using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ActorPlus.Controllers;

[ApiController]
[Route("ActorPlus/assets")]
public class AssetsController : ControllerBase
{
    [HttpGet("birthage.js")]
    public IActionResult GetJs()
    {
        var text = ReadEmbedded("Jellyfin.Plugin.ActorPlus.Web.birthage.js");
        return Content(text, "application/javascript", Encoding.UTF8);
    }

    [HttpGet("birthage.css")]
    public IActionResult GetCss()
    {
        var text = ReadEmbedded("Jellyfin.Plugin.ActorPlus.Web.birthage.css");
        return Content(text, "text/css", Encoding.UTF8);
    }

    private static string ReadEmbedded(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
