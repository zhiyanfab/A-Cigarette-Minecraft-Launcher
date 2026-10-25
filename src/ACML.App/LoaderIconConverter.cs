using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ACML.App;

public sealed class LoaderIconConverter:IValueConverter
{
    private static readonly Dictionary<string,ImageSource> Cache=new();
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
    {
        var loader=value is ACML.Core.GameVersion version ? version.Loader : value as string??"原版";
        if(Cache.TryGetValue(loader,out var image))return image;
        var asset=loader switch{"Fabric"=>"fabric.png",_=>null};
        if(loader=="NeoForge") image=FoxHead();
        else if(loader is "Forge" or "Forge / Legacy") image=AnvilBlock();
        else if(asset is not null){var bitmap=new BitmapImage(new Uri("pack://application:,,,/ACML;component/Assets/"+asset));bitmap.Freeze();image=bitmap;}
        else image=MinecraftIconRenderer.GrassBlock();
        Cache[loader]=image;return image;
    }
    private static ImageSource FoxHead()=>MinecraftIconRenderer.FoxHead();
    private static ImageSource AnvilBlock()=>MinecraftIconRenderer.Anvil();
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}


