using System.Reflection;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Widgets;

namespace ObsPlugin.Tests;

public class PluginSettingsTests
{
    [Fact]
    public void Conexao_fica_na_pagina_do_plugin()
    {
        var attribute = typeof(ObsWidgetBase).Assembly.GetCustomAttribute<OpenWigiPluginSettingsAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("ObsPluginSettings", attribute.ComponentType.Name);
        Assert.True(typeof(PluginSettingsComponent).IsAssignableFrom(attribute.ComponentType));
    }
}
