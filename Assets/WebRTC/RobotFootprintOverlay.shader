Shader "Insta360/Robot Footprint Overlay"
{
    // Flat, semi-transparent rectangle with a distance grid and a solid
    // border, used to render Husky's footprint as a scale/clearance
    // reference inside the 360 video sphere. See HuskyBodyReferenceOverlay.cs.
    Properties
    {
        _Color ("Fill Color", Color) = (0, 1, 1, 0.18)
        _LineColor ("Grid/Border Color", Color) = (0, 1, 1, 0.85)
        // Local +Y of the quad (pre-rotation) is mapped to local +Z (the
        // anchor's forward) by the 90-degree X rotation applied in
        // HuskyBodyReferenceOverlay.cs -- so "front" here always means the
        // +meters.y half, matching whatever the anchor's forward direction
        // actually is (the camera's own forward, which is what the video
        // itself is oriented around).
        _FrontColor ("Front Edge Color", Color) = (0.3, 1, 0.2, 1)
        _WorldSize ("World Size (width, length)", Vector) = (0.67, 0.99, 0, 0)
        _GridSpacing ("Grid Spacing (m)", Float) = 0.25
        _LineWidth ("Grid Line Width (m)", Float) = 0.015
        _BorderWidth ("Border Width (m)", Float) = 0.03
        _FrontBorderWidth ("Front Edge Width (m)", Float) = 0.05
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        // The panorama is a background even when its mesh is closer than the footprint.
        ZTest Always
        ZWrite Off
        Lighting Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VertexToFragment
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            fixed4 _LineColor;
            fixed4 _FrontColor;
            float4 _WorldSize;
            float _GridSpacing;
            float _LineWidth;
            float _BorderWidth;
            float _FrontBorderWidth;

            VertexToFragment vert(AppData input)
            {
                VertexToFragment output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(VertexToFragment, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fixed4 frag(VertexToFragment input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Quad UV is 0..1; recenter to +/- half the real-world size
                // (in meters) so grid/border math works in physical units
                // regardless of how big the footprint rectangle actually is.
                float2 meters = (input.uv - 0.5) * _WorldSize.xy;

                float2 gridPhase = frac(meters / _GridSpacing + 0.5);
                float2 distToGridLine = min(gridPhase, 1 - gridPhase) * _GridSpacing;
                float gridMask = step(min(distToGridLine.x, distToGridLine.y), _LineWidth);

                float2 distToEdge = _WorldSize.xy * 0.5 - abs(meters);
                float borderMask = step(min(distToEdge.x, distToEdge.y), _BorderWidth);

                // Front edge: only the +meters.y side (see comment above
                // _FrontColor), drawn thicker and in a different color so
                // "which way is forward" reads at a glance instead of the
                // rectangle looking symmetric on all four sides.
                float frontHalf = step(0.0, meters.y);
                float nearFrontEdge = step(distToEdge.y, _FrontBorderWidth);
                float frontEdgeMask = frontHalf * nearFrontEdge;

                fixed4 col = _Color;
                col = lerp(col, _LineColor, gridMask);
                col = lerp(col, _LineColor, borderMask);
                col = lerp(col, _FrontColor, frontEdgeMask);
                return col;
            }
            ENDCG
        }
    }
}
