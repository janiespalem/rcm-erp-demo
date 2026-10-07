using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed class LegoViewport : Grid
{
    private readonly Viewport3D viewport = new();
    private readonly ModelVisual3D visual = new();
    private readonly PerspectiveCamera camera = new() { FieldOfView = 45, NearPlaneDistance = .01, FarPlaneDistance = 10000 };
    private Point? drag;
    private Point3D center;
    private double radius = 10;
    private double yaw = 2.1;
    private double pitch = .55;
    public LegoViewport()
    {
        Background = new SolidColorBrush(Color.FromRgb(12, 30, 24)); ClipToBounds = true; viewport.Camera = camera; viewport.Children.Add(visual); Children.Add(viewport);
        MouseLeftButtonDown += (_, e) => { drag = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseLeftButtonUp += (_, _) => { drag = null; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => drag = null;
        MouseMove += (_, e) => { if (drag is not { } start) return; var next = e.GetPosition(this); yaw -= (next.X - start.X) * .008; pitch = Math.Clamp(pitch + (next.Y - start.Y) * .008, .05, 1.5); drag = next; UpdateCamera(); };
        MouseWheel += (_, e) => { radius = Math.Clamp(radius * Math.Exp(-e.Delta / 120d * .12), .15, 3000); UpdateCamera(); e.Handled = true; };
    }
    public Model3DGroup? Scene => visual.Content as Model3DGroup;
    public void Clear() => visual.Content = null;
    public async Task SetPlan(LegoPlan plan, int depthCm, CancellationToken ct)
    {
        var scene = await Task.Run(() => Build(plan, depthCm, ct), ct); ct.ThrowIfCancellationRequested(); visual.Content = scene; ResetCamera();
    }
    public void ResetCamera()
    {
        if (visual.Content is not { } model) return; var bounds = model.Bounds; if (bounds.IsEmpty) return;
        center = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        radius = Math.Max(2, Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) * 1.8); yaw = 2.1; pitch = .55; UpdateCamera();
    }
    private void UpdateCamera()
    {
        var position = center + new Vector3D(Math.Cos(yaw) * Math.Cos(pitch) * radius, Math.Sin(pitch) * radius, Math.Sin(yaw) * Math.Cos(pitch) * radius);
        camera.Position = position; camera.LookDirection = center - position; camera.UpDirection = new(0, 1, 0);
    }
    internal static Model3DGroup Build(LegoPlan plan, int depthCm, CancellationToken ct)
    {
        var scene = new Model3DGroup(); scene.Children.Add(new AmbientLight(Color.FromRgb(130, 140, 135))); scene.Children.Add(new DirectionalLight(Colors.White, new(-1, -2, -1)));
        foreach (var group in plan.Blocks.GroupBy(block => block.LengthCm))
        {
            var mesh = new MeshGeometry3D();
            foreach (var block in group)
            {
                ct.ThrowIfCancellationRequested(); var x = (block.Xcm + 1) / 100d; var y = (block.Ycm + 1) / 100d; var z = (block.Zcm + 1) / 100d;
                Cube(mesh, x, y, z, Math.Max(1, block.WidthCm - 2) / 100d, Math.Max(1, block.HeightCm - 2) / 100d, Math.Max(1, block.DepthCm - 2) / 100d);
            }
            var material = new DiffuseMaterial(new SolidColorBrush(LegoPlanView.ColorFor(group.Key))); scene.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }
        if (plan.Input.WithArch && plan.Blocks.Length > 0)
        {
            var roof = new MeshGeometry3D();
            if (plan.Input.Shape == "U") Arch(roof, 0, plan.DimensionsCm[1] / 100d, plan.DimensionsCm[0] / 100d, plan.ActualHeightCm / 100d, depthCm / 100d);
            else if (plan.Input.Shape == "boksy")
                for (var index = 0; index < plan.Input.Boxes; index++) { ct.ThrowIfCancellationRequested(); Arch(roof, index * (plan.DimensionsCm[0] + depthCm) / 100d, plan.DimensionsCm[0] / 100d, plan.DimensionsCm[1] / 100d, plan.ActualHeightCm / 100d, depthCm / 100d); }
            var material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(215, 210, 220, 215))); scene.Children.Add(new GeometryModel3D(roof, material) { BackMaterial = material });
        }
        scene.Freeze(); return scene;
    }
    private static void Quad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        var offset = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        foreach (var index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(offset + index);
    }
    private static void Cube(MeshGeometry3D mesh, double x, double y, double z, double w, double h, double d)
    {
        var a = new Point3D(x,y,z); var b = new Point3D(x+w,y,z); var c = new Point3D(x+w,y+h,z); var e = new Point3D(x,y+h,z);
        var f = new Point3D(x,y,z+d); var g = new Point3D(x+w,y,z+d); var i = new Point3D(x+w,y+h,z+d); var j = new Point3D(x,y+h,z+d);
        Quad(mesh,a,b,c,e); Quad(mesh,g,f,j,i); Quad(mesh,f,a,e,j); Quad(mesh,b,g,i,c); Quad(mesh,e,c,i,j); Quad(mesh,f,g,b,a);
    }
    private static void Arch(MeshGeometry3D mesh, double offset, double span, double depth, double wallHeight, double legWidth)
    {
        const int steps = 32; legWidth = Math.Min(legWidth, span / 3);
        Point3D Outer(double t, double z) => new(offset + span * t, wallHeight + .131 + .521 * (1 - Math.Pow(2 * t - 1, 2)), z);
        Point3D Inner(double t, double z) => new(offset + legWidth + (span - 2 * legWidth) * t, wallHeight + .343 * (1 - Math.Pow(2 * t - 1, 2)), z);
        for (var index = 0; index < steps; index++)
        {
            var a = index / (double)steps; var b = (index + 1) / (double)steps;
            Quad(mesh, Outer(a,0), Outer(b,0), Outer(b,depth), Outer(a,depth)); Quad(mesh, Inner(b,0), Inner(a,0), Inner(a,depth), Inner(b,depth));
            Quad(mesh, Inner(a,0), Inner(b,0), Outer(b,0), Outer(a,0)); Quad(mesh, Inner(b,depth), Inner(a,depth), Outer(a,depth), Outer(b,depth));
        }
        Quad(mesh, Inner(0,0), Outer(0,0), Outer(0,depth), Inner(0,depth)); Quad(mesh, Outer(1,0), Inner(1,0), Inner(1,depth), Outer(1,depth));
    }
}
