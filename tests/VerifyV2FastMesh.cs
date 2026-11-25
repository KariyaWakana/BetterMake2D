namespace BetterMake2D;

public static class VerifyV2FastMesh
{
    public static string Run() => VerifyV2Perspective.Check(V2Accuracy.Fast) +
        System.Environment.NewLine + VerifyV2ClipPlanes.Check(V2Accuracy.Fast) +
        System.Environment.NewLine + VerifyV2TangentEdges.CheckAccuracy(V2Accuracy.Fast);
}
