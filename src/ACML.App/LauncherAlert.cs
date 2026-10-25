using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ACML.App;
public sealed class LauncherAlert : Window
{
    public LauncherAlert(string title,string message)
    {
        Title=title;Width=500;SizeToContent=SizeToContent.Height;MaxHeight=560;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.None;
        Background=Brushes.Transparent;AllowsTransparency=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        FontFamily=new FontFamily("Microsoft YaHei UI");Foreground=new SolidColorBrush(Color.FromRgb(37,44,55));
        var panel=new StackPanel();var heading=new TextBlock { Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap };
        heading.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==1)DragMove();};panel.Children.Add(heading);
        panel.Children.Add(new ScrollViewer { Content=new TextBlock { Text=message,FontSize=13,TextWrapping=TextWrapping.Wrap },MaxHeight=340,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,18,0,22) });
        var close=new Button { Content="确定",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(24,10,24,10),IsDefault=true,IsCancel=true };
        if(Application.Current?.TryFindResource("Primary") is Style style)close.Style=style;
        close.Click+=(_,_)=>Close();panel.Children.Add(close);
        Content=new Border { Child=panel,Background=new SolidColorBrush(Color.FromRgb(250,251,252)),BorderBrush=new SolidColorBrush(Color.FromRgb(222,225,229)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(28) };
    }
    public static void Show(string title,string message)
    {
        var dialog=new LauncherAlert(title,message);
        if(Application.Current?.MainWindow is Window owner&&owner.IsVisible) { dialog.Owner=owner;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner; }
        dialog.ShowDialog();
    }
}
