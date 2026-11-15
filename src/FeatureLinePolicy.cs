using System;
using System.Linq;
using Rhino.Geometry;

namespace Project2.VisibleLines
{
    public enum FeatureLine
    {
        Silhouette,
        Section,
        SharpEdge,
        SmoothTransition,
        ParameterSeam,
        NeedsReview
    }

    public static class FeatureLinePolicy
    {
        public static FeatureLine Classify(SilhouetteType sourceType, Brep brep, ComponentIndex sourceComponent,
            double smoothAngleRadians = Math.PI / 180)
        {
            if ((sourceType & SilhouetteType.SectionCut) != 0)
                return FeatureLine.Section;

            const SilhouetteType outline = SilhouetteType.Tangent | SilhouetteType.Projecting |
                SilhouetteType.Crease | SilhouetteType.Boundary;
            if ((sourceType & outline) != 0)
                return FeatureLine.Silhouette;

            if ((sourceType & SilhouetteType.NonSilhouetteSeam) != 0)
                return FeatureLine.ParameterSeam;

            if (brep != null && sourceComponent.ComponentIndexType == ComponentIndexType.BrepEdge &&
                sourceComponent.Index >= 0 && sourceComponent.Index < brep.Edges.Count)
            {
                var edge = brep.Edges[sourceComponent.Index];
                var trims = edge.TrimIndices().Select(i => brep.Trims[i]).ToArray();
                if (trims.Length == 2 && trims.All(t => t.TrimType == BrepTrimType.Seam) &&
                    trims[0].Face.FaceIndex == trims[1].Face.FaceIndex)
                    return FeatureLine.ParameterSeam;

                if (edge.IsSmoothManifoldEdge(smoothAngleRadians))
                    return FeatureLine.SmoothTransition;
            }

            if ((sourceType & SilhouetteType.NonSilhouetteTangent) != 0)
                return FeatureLine.SmoothTransition;
            if ((sourceType & SilhouetteType.NonSilhouetteCrease) != 0)
                return FeatureLine.SharpEdge;
            return FeatureLine.NeedsReview;
        }

        public static bool ShouldDraw(FeatureLine kind, bool showSmoothTransitions = false)
        {
            return kind == FeatureLine.Silhouette || kind == FeatureLine.Section ||
                kind == FeatureLine.SharpEdge || (showSmoothTransitions && kind == FeatureLine.SmoothTransition);
        }
    }
}
