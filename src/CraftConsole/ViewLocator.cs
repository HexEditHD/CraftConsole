using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftConsole.ViewModels;

namespace CraftConsole;

/// <summary>
/// Resolves ViewModels to their corresponding Views by convention:
/// Foo.ViewModels.FooViewModel → Foo.Views.FooView
/// Works across assemblies loaded into the AppDomain.
/// </summary>
[RequiresUnreferencedCode(
    "ViewLocator uses reflection for type resolution.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        var vmType = param.GetType();
        var viewTypeName = vmType.FullName!.Replace("ViewModels", "Views", StringComparison.Ordinal)
                                           .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(viewTypeName))
            .FirstOrDefault(t => t is not null);

        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = $"View not found: {viewTypeName}" };
    }

    public bool Match(object? data) => data is ObservableObject;
}
