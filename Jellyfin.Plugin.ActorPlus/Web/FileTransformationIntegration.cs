using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Web;

/// <summary>
/// Registers an in-memory transformation for <c>jellyfin-web/index.html</c> via
/// <c>jellyfin-plugin-file-transformation</c>.
///
/// This avoids writing to <c>/usr/share/jellyfin/web/index.html</c> (which fails when Jellyfin
/// runs under a non-root account).
/// </summary>
internal static class FileTransformationIntegration
{
    private const string FileTransformationAssemblyName = "Jellyfin.Plugin.FileTransformation";
    private const string FileTransformationPluginTypeName = "Jellyfin.Plugin.FileTransformation.FileTransformationPlugin";

    private const string WebWriteServiceTypeName = "Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService";
    private const string TransformFileDelegateTypeName = "Jellyfin.Plugin.FileTransformation.Library.TransformFile";

    public static bool TryRegisterIndexHtmlTransformation(Guid transformationId, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            var ftAssembly = FindFileTransformationAssembly();

            var writeServiceType = ftAssembly?.GetType(WebWriteServiceTypeName, throwOnError: false, ignoreCase: false)
                                   ?? GetTypeFromLoadedAssemblies(WebWriteServiceTypeName, FileTransformationAssemblyName);
            var transformDelegateType = ftAssembly?.GetType(TransformFileDelegateTypeName, throwOnError: false, ignoreCase: false)
                                      ?? GetTypeFromLoadedAssemblies(TransformFileDelegateTypeName, FileTransformationAssemblyName);

            if (writeServiceType is null || transformDelegateType is null)
            {
                logger.LogInformation(
                    "ActorPlus: File Transformation plugin is not installed; Web UI enhancements are disabled. " +
                    "Install 'jellyfin-plugin-file-transformation' to enable in-memory index.html modifications.");
                return false;
            }

            var writeService = serviceProvider.GetService(writeServiceType);
            if (writeService is null)
            {
                logger.LogWarning(
                    "ActorPlus: File Transformation types found, but IWebFileTransformationWriteService is not available from the host container. " +
                    "This usually indicates an AssemblyLoadContext type mismatch or an incompatible File Transformation plugin build. " +
                    "Web UI enhancements are disabled.");
                return false;
            }

            var method = typeof(WebUiStreamTransformer).GetMethod(
                nameof(WebUiStreamTransformer.TransformIndexHtmlStream),
                BindingFlags.Public | BindingFlags.Static);

            if (method is null)
            {
                logger.LogWarning("ActorPlus: WebUiStreamTransformer.TransformIndexHtmlStream not found; Web UI enhancements are disabled.");
                return false;
            }

            var transformDelegate = Delegate.CreateDelegate(transformDelegateType, method);

            var update = writeServiceType.GetMethod("UpdateTransformation", BindingFlags.Public | BindingFlags.Instance);
            if (update is not null)
            {
                update.Invoke(writeService, new object[] { transformationId, "index.html", transformDelegate });
            }
            else
            {
                var add = writeServiceType.GetMethod("AddTransformation", BindingFlags.Public | BindingFlags.Instance);
                add?.Invoke(writeService, new object[] { transformationId, "index.html", transformDelegate });
            }

            logger.LogInformation("ActorPlus: registered in-memory index.html transformation via File Transformation plugin.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ActorPlus: failed to register index.html transformation via File Transformation plugin.");
            return false;
        }
    }

    public static void TryUnregisterIndexHtmlTransformation(Guid transformationId, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            var writeServiceType = GetTypeFromLoadedAssemblies(WebWriteServiceTypeName, FileTransformationAssemblyName);
            if (writeServiceType is null)
            {
                return;
            }

            var writeService = serviceProvider.GetService(writeServiceType);
            if (writeService is null)
            {
                return;
            }

            var remove = writeServiceType.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Instance);
            remove?.Invoke(writeService, new object[] { transformationId });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ActorPlus: failed to unregister index.html transformation (non-fatal). ");
        }
    }

    private static Type? GetTypeFromLoadedAssemblies(string fullName, string assemblyName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    private static Assembly? FindFileTransformationAssembly()
    {
        try
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                foreach (var asm in alc.Assemblies)
                {
                    if (!string.Equals(asm.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pluginType = asm.GetType(FileTransformationPluginTypeName, throwOnError: false, ignoreCase: false);
                    if (pluginType is null)
                    {
                        continue;
                    }

                    var instanceProp = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    var instance = instanceProp?.GetValue(null);
                    if (instance is not null)
                    {
                        return asm;
                    }
                }
            }
        }
        catch
        {
            // ignore and fall back
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase));
    }
}
