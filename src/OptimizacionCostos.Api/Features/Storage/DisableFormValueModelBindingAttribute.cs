using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace OptimizacionCostos.Api.Features.Storage;

/// <summary>
/// Saca los proveedores de valores de FORM (y solo esos) de la construcción del model binding
/// para la acción decorada. Ruta/query/header siguen bindeando normal; lo único que deja de
/// pasar es que MVC llame a Request.ReadFormAsync() por su cuenta.
///
/// Por qué hace falta incluso sin IFormFile en la firma: el composite value provider de una
/// acción se construye UNA vez, para el conjunto completo de parámetros, invocando a TODOS los
/// IValueProviderFactory registrados (FormValueProviderFactory entre ellos) — no solo a los que
/// el parámetro en cuestión necesita. FormValueProviderFactory mira únicamente
/// HttpRequest.HasFormContentType; si es true, llama a ReadFormAsync() sin importar si algún
/// parámetro pide binding desde el form. O sea que sacar IFormFile de la firma de Subir() NO
/// alcanza por sí solo: clientId/kind (bindeados por ruta) igual disparan la construcción del
/// value provider compartido, y esa construcción igual lee el form completo si esta clase no
/// se aplica. Con esta clase, la primera lectura real del cuerpo es la que hace el propio método
/// (Request.ReadFormAsync), después de los guards. Port del patrón documentado por Microsoft
/// para cargas grandes (ASP.NET Core docs, "Uploading large files with streaming").
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        RemoveType<FormValueProviderFactory>(factories);
        RemoveType<FormFileValueProviderFactory>(factories);
        RemoveType<JQueryFormValueProviderFactory>(factories);
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    private static void RemoveType<TFactory>(IList<IValueProviderFactory> factories) where TFactory : IValueProviderFactory
    {
        for (var i = factories.Count - 1; i >= 0; i--)
        {
            if (factories[i] is TFactory) factories.RemoveAt(i);
        }
    }
}
