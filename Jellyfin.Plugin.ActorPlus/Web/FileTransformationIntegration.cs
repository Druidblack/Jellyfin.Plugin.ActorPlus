using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Web;

/// <summary>
/// Registers Actor Plus web-client patches with the File Transformation plugin.
///
/// File Transformation runs in a separate AssemblyLoadContext, so Actor Plus must not
/// reference its types directly. Registration is performed through the plugin's public
/// reflection API and the payload object is created using File Transformation's own
/// Newtonsoft.Json JObject type.
/// </summary>
internal static class FileTransformationIntegration
{
    private const string FileTransformationAssemblyName = "Jellyfin.Plugin.FileTransformation";
    private const string PluginInterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";
    private const string RegisterMethodName = "RegisterTransformation";

    public static bool TryRegisterIndexHtmlTransformation(Guid transformationId, ILogger logger)
    {
        try
        {
            Assembly? fileTransformationAssembly = FindFileTransformationAssembly();
            if (fileTransformationAssembly is null)
            {
                logger.LogWarning(
                    "ActorPlus: File Transformation was not found. Install File Transformation 3.0.0.0 or newer and restart Jellyfin to enable Actor Plus web overlays.");
                return false;
            }

            Type? pluginInterfaceType = fileTransformationAssembly.GetType(
                PluginInterfaceTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (pluginInterfaceType is null)
            {
                logger.LogWarning(
                    "ActorPlus: File Transformation assembly {Version} does not expose {PluginInterface}; web overlays are disabled.",
                    fileTransformationAssembly.GetName().Version,
                    PluginInterfaceTypeName);
                return false;
            }

            MethodInfo? registerMethod = pluginInterfaceType.GetMethod(
                RegisterMethodName,
                BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                logger.LogWarning(
                    "ActorPlus: File Transformation {Version} does not expose PluginInterface.RegisterTransformation; web overlays are disabled.",
                    fileTransformationAssembly.GetName().Version);
                return false;
            }

            ParameterInfo[] parameters = registerMethod.GetParameters();
            if (parameters.Length != 1)
            {
                logger.LogWarning(
                    "ActorPlus: unsupported File Transformation RegisterTransformation signature ({ParameterCount} parameters).",
                    parameters.Length);
                return false;
            }

            Type payloadType = parameters[0].ParameterType;
            MethodInfo? parseMethod = payloadType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            if (parseMethod is null)
            {
                logger.LogWarning(
                    "ActorPlus: could not create a File Transformation registration payload of type {PayloadType}.",
                    payloadType.FullName);
                return false;
            }

            var registration = new
            {
                id = transformationId.ToString(),
                fileNamePattern = "index.html",
                callbackAssembly = typeof(ActorPlusTransformationPatches).Assembly.FullName,
                callbackClass = typeof(ActorPlusTransformationPatches).FullName,
                callbackMethod = nameof(ActorPlusTransformationPatches.IndexHtml)
            };

            string json = JsonSerializer.Serialize(registration);
            object? payload = parseMethod.Invoke(null, new object[] { json });
            if (payload is null)
            {
                logger.LogWarning("ActorPlus: File Transformation registration payload could not be created.");
                return false;
            }

            object? result = registerMethod.Invoke(null, new[] { payload });
            if (result is bool registered && !registered)
            {
                logger.LogWarning("ActorPlus: File Transformation rejected the index.html transformation registration.");
                return false;
            }

            logger.LogInformation(
                "ActorPlus: registered index.html transformation with File Transformation {Version}.",
                fileTransformationAssembly.GetName().Version);
            return true;
        }
        catch (TargetInvocationException ex)
        {
            logger.LogError(
                ex.InnerException ?? ex,
                "ActorPlus: File Transformation threw an error while registering the index.html transformation.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ActorPlus: failed to register the index.html transformation with File Transformation.");
            return false;
        }
    }

    private static Assembly? FindFileTransformationAssembly()
    {
        return AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase))
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase));
    }
}
