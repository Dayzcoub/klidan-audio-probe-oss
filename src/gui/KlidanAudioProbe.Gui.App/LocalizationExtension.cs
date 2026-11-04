using System.Windows.Markup;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.App;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;
    public string Key { get; set; } = string.Empty;
    public override object ProvideValue(IServiceProvider serviceProvider) => Localization.Get(Key);
}
