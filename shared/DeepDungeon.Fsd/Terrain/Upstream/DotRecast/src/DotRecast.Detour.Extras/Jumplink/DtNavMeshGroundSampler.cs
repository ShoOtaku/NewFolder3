using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace DotRecast.Detour.Extras.Jumplink;

public class DtNavMeshGroundSampler : DtAbstractGroundSampler
{
    public override void Sample
    (
        DtJumpLinkBuilderConfig acfg,
        RcBuilderResult         result,
        DtEdgeSampler           es
    )
    {
        var navMeshQuery = CreateQuery(result, acfg.agentRadius, acfg.agentHeight, acfg.agentClimb);
        Sample(acfg, es, navMeshQuery);
    }

    public void Sample
    (
        DtJumpLinkBuilderConfig acfg,
        DtEdgeSampler           es,
        DtNavMeshQuery          navMeshQuery
    )
    {
        if (navMeshQuery == null)
            return;

        SampleGround(acfg, es, (pt, heightRange, out height) => GetNavMeshHeight(navMeshQuery, pt, acfg.cellSize, heightRange, out height));
    }

    public static DtNavMeshQuery CreateQuery
    (
        RcBuilderResult r,
        float           agentRadius,
        float           agentHeight,
        float           agentClimb
    )
    {
        var option = new DtNavMeshCreateParams
        {
            verts          = r.Mesh.verts,
            vertCount      = r.Mesh.nverts,
            polys          = r.Mesh.polys,
            polyAreas      = r.Mesh.areas,
            polyFlags      = r.Mesh.flags,
            polyCount      = r.Mesh.npolys,
            nvp            = r.Mesh.nvp,
            walkableRadius = agentRadius,
            walkableHeight = agentHeight,
            walkableClimb  = agentClimb,
            bmin           = r.Mesh.bmin,
            bmax           = r.Mesh.bmax,
            cs             = r.Mesh.cs,
            ch             = r.Mesh.ch,
            buildBvTree    = true
        };

        if (r.MeshDetail != null)
        {
            option.detailMeshes     = r.MeshDetail.meshes;
            option.detailVerts      = r.MeshDetail.verts;
            option.detailVertsCount = r.MeshDetail.nverts;
            option.detailTris       = r.MeshDetail.tris;
            option.detailTriCount   = r.MeshDetail.ntris;
        }

        var mesh   = new DtNavMesh();
        var status = mesh.Init(DtNavMeshBuilder.CreateNavMeshData(option), option.nvp, 0);
        return status.Failed() ?
                   null :
                   new DtNavMeshQuery(mesh);
    }


    private static bool GetNavMeshHeight
    (
        DtNavMeshQuery navMeshQuery,
        RcVec3f        pt,
        float          cs,
        float          heightRange,
        out float      height
    )
    {
        height = 0;

        var halfExtents = new RcVec3f(cs, heightRange, cs);
        var maxHeight   = pt.Y + heightRange;
        var found       = new RcAtomicBoolean();
        var minHeight   = new RcAtomicFloat(pt.Y);

        var query = new DtCallbackPolyQuery(UpdateMinHeight);

        navMeshQuery.QueryPolygons(pt, halfExtents, DtQueryNoOpFilter.Shared, query);

        if (found.Get())
        {
            height = minHeight.Get();
            return true;
        }

        height = pt.Y;
        return false;

        void UpdateMinHeight
        (
            DtMeshTile tile,
            DtPoly     poly,
            long       refs
        )
        {
            var status = navMeshQuery.GetPolyHeight(refs, pt, out var h);

            if (status.Succeeded())
            {
                if (h > minHeight.Get() && h < maxHeight)
                {
                    minHeight.Exchange(h);
                    found.Set(true);
                }
            }
        }
    }
}
