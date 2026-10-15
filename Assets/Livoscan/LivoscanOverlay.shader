// Overlay shader for the wrist minimap (points, object boxes, robot arrow, dial).
// The Insta360 video sphere is Queue Geometry + ZWrite On and only 0.5 m in radius, so the minimap
// can sit inside or outside it; like RobotFootprintOverlay.shader this draws with ZTest Always and
// ZWrite Off so the video never hides it. Draw order is set per material via renderQueue.
// Optional circular clip: fragments farther than _LivoscanClipRadius from the dial centre (measured
// in the XZ plane of _LivoscanClipMatrix, i.e. the minimap root's local space) are discarded.
Shader "Livoscan/Overlay"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        [Toggle] _UseVertexColor ("Use Vertex Color", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _LivoscanClipRadius ("Clip Radius (0 = off)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull [_Cull]
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

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 dial : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            fixed4 _Color;
            float _UseVertexColor;
            float _LivoscanClipRadius;
            float4x4 _LivoscanClipMatrix;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
                o.dial = mul(_LivoscanClipMatrix, float4(world, 1)).xz;
                fixed4 c = v.color;
            #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb = GammaToLinearSpace(c.rgb);   // mesh colours are sRGB bytes
            #endif
                o.color = lerp(fixed4(1, 1, 1, 1), c, _UseVertexColor) * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_LivoscanClipRadius > 0 && dot(i.dial, i.dial) > _LivoscanClipRadius * _LivoscanClipRadius) discard;
                return i.color;
            }
            ENDCG
        }
    }
}
