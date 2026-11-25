using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2NativeConcurrency
{
    private sealed record Fixture(Brep A, Brep B, Line Line) : IDisposable
    {
        internal Fixture Copy() => new(A.DuplicateBrep(), B.DuplicateBrep(), Line);
        public void Dispose() { A.Dispose(); B.Dispose(); }
    }

    public static string Run()
    {
        const int jobs = 288;
        var originals = Enumerable.Range(0, 18).Select(i =>
        {
            var a = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -3), new Vector3d(.17, .23, 1)), 2), 6)
                .ToBrep(true, true);
            var b = new Cylinder(new Circle(new Plane(new Point3d(i % 4 == 0 ? 7 : i * .19 - 1, -3, 0),
                new Vector3d(.2, 1, .37)), 1.3), 6).ToBrep(true, true);
            return new Fixture(a, b, new Line(new Point3d(i * .25 - 2, .2, 15), new Point3d(i * .25 - 2, .2, -15)));
        }).ToArray();
        try
        {
            var notes = new System.Collections.Generic.List<string>();
            foreach (bool bodies in new[] { true, false })
            {
                string[] reference = null, geometryReference = null;
                foreach (int lanes in new[] { 1, 1, 2, 4, 6 })
                {
                    var isolated = Enumerable.Range(0, lanes).Select(_ => originals.Select(f => f.Copy()).ToArray()).ToArray();
                    var result = new string[jobs];
                    var geometry = new string[jobs];
                    int callsActive = 0, maximum = 0;
                    try
                    {
                        long start = Stopwatch.GetTimestamp();
                        if (lanes == 1) Work(0);
                        else Parallel.For(0, lanes, new ParallelOptions { MaxDegreeOfParallelism = lanes }, Work);
                        var span = Stopwatch.GetElapsedTime(start);
                        reference ??= result;
                        geometryReference ??= geometry;
                        int differences = Enumerable.Range(0, jobs).Count(i => reference[i] != result[i]);
                        int differentGeometry = Enumerable.Range(0, jobs).Count(i => geometryReference[i] != geometry[i]);
                        notes.Add($"{(bodies ? "BrepBrep" : "CurveBrepFace")} {lanes} lanes, observed {maximum}, " +
                            $"{jobs} calls, {span.TotalSeconds:F3} s, {differences} CRC and {differentGeometry} geometry-signature differences.");
                        if (differentGeometry != 0 || !bodies && differences != 0)
                            throw new InvalidOperationException(string.Join(Environment.NewLine, notes));

                        void Work(int lane)
                        {
                            for (int i = lane; i < jobs; i += lanes)
                            {
                                var source = isolated[lane][i % originals.Length];
                                Curve[] curves;
                                Point3d[] points;
                                bool ok;
                                int now = Interlocked.Increment(ref callsActive);
                                while (true)
                                {
                                    int seen = Volatile.Read(ref maximum);
                                    if (seen >= now || Interlocked.CompareExchange(ref maximum, now, seen) == seen) break;
                                }
                                try
                                {
                                    if (bodies) ok = Intersection.BrepBrep(source.A, source.B, .0001, out curves, out points);
                                    else
                                    {
                                        using var ray = new LineCurve(source.Line);
                                        ok = Intersection.CurveBrepFace(ray, source.A.Faces[0], .0001, out curves, out points);
                                    }
                                }
                                finally { Interlocked.Decrement(ref callsActive); }
                                var outputCurves = curves ?? Array.Empty<Curve>();
                                uint[] crc = outputCurves.Select(c => c.DataCRC(0)).ToArray();
                                geometry[i] = ok + "|" + string.Join(";", outputCurves.Select(CurveShape).Order(StringComparer.Ordinal)) + "|" +
                                    string.Join(";", (points ?? Array.Empty<Point3d>()).Select(PointShape).Order(StringComparer.Ordinal));
                                foreach (var curve in outputCurves) curve.Dispose();
                                result[i] = string.Join(",", crc) + "|" + ok + "|" + string.Join(";", (points ?? Array.Empty<Point3d>())
                                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}")));
                            }
                        }
                    }
                    finally { foreach (var group in isolated) foreach (var fixture in group) fixture.Dispose(); }
                }
            }
            return string.Join(System.Environment.NewLine, notes);
        }
        finally { foreach (var fixture in originals) fixture.Dispose(); }
    }

    private static string PointShape(Point3d p) => string.Create(CultureInfo.InvariantCulture, $"{p.X:F6},{p.Y:F6},{p.Z:F6}");

    private static string CurveShape(Curve curve)
    {
        var box = curve.GetBoundingBox(true);
        return $"{curve.IsClosed}:{PointShape(box.Min)}:{PointShape(box.Max)}:{curve.GetLength():F6}";
    }
}

