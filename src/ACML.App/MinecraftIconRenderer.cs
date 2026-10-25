using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace ACML.App;
public static class MinecraftIconRenderer
{
    private static BitmapImage Texture(string asset)
    { var bitmap = new BitmapImage(new Uri("pack://application:,,,/ACML;component/Assets/" + asset)); bitmap.Freeze(); return bitmap; }
    public static ImageSource FoxHead()
    {
        var texture = Texture("minecraft-fox.png"); var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(visual,EdgeMode.Aliased);
        var pixels=new byte[texture.PixelWidth*texture.PixelHeight*4];
        var rgba=new FormatConvertedBitmap(texture,PixelFormats.Bgra32,null,0);rgba.CopyPixels(pixels,texture.PixelWidth*4,0);
        using var context = visual.RenderOpen();
        // Vanilla fox head, ears and muzzle UVs. The original PNG stays unchanged.
        void Face(Rect uv, Rect destination)
        {
            // Draw original texels on the pixel grid instead of filtering across
            // neighbouring faces in the entity texture atlas.
            for(var y=0;y<(int)uv.Height;y++)for(var x=0;x<(int)uv.Width;x++)
            {
                var i=(((int)uv.Y+y)*texture.PixelWidth+(int)uv.X+x)*4;
                if(pixels[i+3]==0)continue;
                var brush=new SolidColorBrush(Color.FromArgb(pixels[i+3],pixels[i+2],pixels[i+1],pixels[i]));brush.Freeze();
                context.DrawRectangle(brush,null,new Rect(destination.X+x*destination.Width/uv.Width,destination.Y+y*destination.Height/uv.Height,destination.Width/uv.Width,destination.Height/uv.Height));
            }
        }
        Face(new(7,11,8,6),new(0,6,32,24));
        Face(new(9,2,2,2),new(0,0,8,8)); Face(new(16,2,2,2),new(24,0,8,8));
        Face(new(9,21,4,2),new(8,22,16,8));
        context.Close(); var image=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    public static ImageSource Anvil()
    {
        var body=Texture("minecraft-anvil.png"); var top=Texture("minecraft-anvil-top.png");
        return Block("minecraft-anvil-template.json",new Dictionary<string,BitmapSource>{{"#body",body},{"#top",top}});
    }
    public static ImageSource GrassBlock()
    {
        BitmapSource Tint(BitmapSource source)
        {
            var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0); var pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);
            for(var i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(pixels[i]*89/255);pixels[i+1]=(byte)(pixels[i+1]*189/255);pixels[i+2]=(byte)(pixels[i+2]*145/255);}
            var result=BitmapSource.Create(image.PixelWidth,image.PixelHeight,96,96,PixelFormats.Bgra32,null,pixels,image.PixelWidth*4);result.Freeze();return result;
        }
        var side=Texture("minecraft-grass-side.png");var overlay=Tint(Texture("minecraft-grass-overlay.png"));
        byte[] Data(BitmapSource source){var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);return bytes;}
        var basePixels=Data(side);var layer=Data(overlay);
        for(var i=0;i<basePixels.Length;i+=4){var alpha=layer[i+3];for(var c=0;c<3;c++)basePixels[i+c]=(byte)((layer[i+c]*alpha+basePixels[i+c]*(255-alpha))/255);}
        var combined=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,basePixels,64);combined.Freeze();
        return Block("minecraft-grass-model.json",new Dictionary<string,BitmapSource>{{"#bottom",Texture("minecraft-dirt.png")},{"#top",Tint(Texture("minecraft-grass-top.png"))},{"#side",combined}});
    }
    private static ImageSource Block(string modelFile,IReadOnlyDictionary<string,BitmapSource> textures)
    {
        var scene=new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(145,145,145)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(145,145,145),new Vector3D(1,-2,1)));
        using var source=Application.GetResourceStream(new Uri("pack://application:,,,/ACML;component/Assets/"+modelFile))!.Stream;
        var model=JsonNode.Parse(source)!;
        foreach(var element in model["elements"]!.AsArray())
        {
            var from=element!["from"]!.AsArray().Select(x=>x!.GetValue<double>()).ToArray();
            var to=element["to"]!.AsArray().Select(x=>x!.GetValue<double>()).ToArray();
            var x0=from[0];var y0=from[1];var z0=from[2];var x1=to[0];var y1=to[1];var z1=to[2];
            foreach(var pair in element["faces"]!.AsObject())
            {
                var texture=pair.Value!["texture"]!.GetValue<string>();if(!textures.TryGetValue(texture,out var bitmapTexture))continue;
                var points=pair.Key switch
                {
                    "up"=>new[]{new Point3D(x0,y1,z0),new(x1,y1,z0),new(x1,y1,z1),new(x0,y1,z1)},
                    "down"=>new[]{new Point3D(x0,y0,z1),new(x1,y0,z1),new(x1,y0,z0),new(x0,y0,z0)},
                    "north"=>new[]{new Point3D(x1,y1,z0),new(x0,y1,z0),new(x0,y0,z0),new(x1,y0,z0)},
                    "south"=>new[]{new Point3D(x0,y1,z1),new(x1,y1,z1),new(x1,y0,z1),new(x0,y0,z1)},
                    "west"=>new[]{new Point3D(x0,y1,z0),new(x0,y1,z1),new(x0,y0,z1),new(x0,y0,z0)},
                    _=>new[]{new Point3D(x1,y1,z1),new(x1,y1,z0),new(x1,y0,z0),new(x1,y0,z1)}
                };
                var uv=pair.Value!["uv"]!.AsArray().Select(x=>x!.GetValue<double>()/16).ToArray();
                var coords=new[]{new Point(uv[0],uv[1]),new(uv[2],uv[1]),new(uv[2],uv[3]),new(uv[0],uv[3])};
                var turn=(pair.Value["rotation"]?.GetValue<int>()??0)/90;
                var mesh=new MeshGeometry3D { Positions=new Point3DCollection(points), TriangleIndices=new Int32Collection([0,2,1,0,3,2]), TextureCoordinates=new PointCollection(Enumerable.Range(0,4).Select(i=>coords[(i+turn)%4])) };
                var brush=new ImageBrush(bitmapTexture) { ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1) }; RenderOptions.SetBitmapScalingMode(brush,BitmapScalingMode.NearestNeighbor);
                var material=new DiffuseMaterial(brush); scene.Children.Add(new GeometryModel3D(mesh,material) { BackMaterial=material });
            }
        }
        var viewport=new Viewport3D { Width=128,Height=128,Camera=new OrthographicCamera(new Point3D(-22,24,-22),new Vector3D(30,-16,30),new Vector3D(0,1,0),24) };
        viewport.Children.Add(new ModelVisual3D { Content=scene }); viewport.Measure(new Size(128,128));viewport.Arrange(new Rect(0,0,128,128));viewport.UpdateLayout();
        var bitmap=new RenderTargetBitmap(128,128,96,96,PixelFormats.Pbgra32);bitmap.Render(viewport);bitmap.Freeze();return bitmap;
    }
}
