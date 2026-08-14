using ComputeSharp;
using ComputeSharp.D2D1;

namespace SightoHear.Shaders
{
#pragma warning disable CS9113 // 参数由渲染器传入，当前着色器逻辑暂未使用，保留为预留扩展接口
    [D2DInputCount(0)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct FluidBackgroundEffect(
        float2 resolution, float time,
        float3 color1, float3 color2, float3 color3, float3 color4,
        float randomValue1, float randomValue2, float randomValue3,
        bool useHSVBlending, bool enableLightWave, bool enableDithering = true) : ID2D1PixelShader
    {
#pragma warning restore CS9113
        private float2 Rotate(float2 p, float a)
        {
            float c = Hlsl.Cos(a);
            float s = Hlsl.Sin(a);
            return new float2(p.X * c - p.Y * s, p.X * s + p.Y * c);
        }

        private float2 F_Hash(float2 p)
        {
            p = new float2(
                Hlsl.Dot(p, new float2(2127.1f, 81.17f)),
                Hlsl.Dot(p, new float2(1269.5f, 283.37f)));
            return Hlsl.Frac(Hlsl.Sin(p) * 43758.5453f);
        }

        private float F_Noise(float2 p)
        {
            float2 i = Hlsl.Floor(p);
            float2 f = Hlsl.Frac(p);
            float2 u = f * f * (3.0f - (2.0f * f));

            float n = Hlsl.Lerp(
                Hlsl.Lerp(
                    Hlsl.Dot(-1.0f + 2.0f * F_Hash(i), f),
                    Hlsl.Dot(-1.0f + 2.0f * F_Hash(i + new float2(1.0f, 0.0f)), f - new float2(1.0f, 0.0f)),
                    u.X),
                Hlsl.Lerp(
                    Hlsl.Dot(-1.0f + 2.0f * F_Hash(i + new float2(0.0f, 1.0f)), f - new float2(0.0f, 1.0f)),
                    Hlsl.Dot(-1.0f + 2.0f * F_Hash(i + new float2(1.0f, 1.0f)), f - new float2(1.0f, 1.0f)),
                    u.X),
                u.Y);
            return 0.5f + (0.5f * n);
        }

        private float Range(float val, float mi, float ma) => val * (ma - mi) + mi;

        private float3 Hsv2Rgb(float3 c)
        {
            float4 k = new float4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
            float3 p = Hlsl.Abs(Hlsl.Frac(c.XXX + k.XYZ) * 6.0f - k.WWW);
            return c.Z * Hlsl.Lerp(k.XXX, Hlsl.Saturate(p - k.XXX), c.Y);
        }

        private float3 Rgb2Hsv(float3 c)
        {
            float4 k = new float4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
            float4 p = Hlsl.Lerp(new float4(c.BG, k.WZ), new float4(c.GB, k.XY), Hlsl.Step(c.B, c.G));
            float4 q = Hlsl.Lerp(new float4(p.XYW, c.R), new float4(c.R, p.YZX), Hlsl.Step(p.X, c.R));

            float d = q.X - Hlsl.Min(q.W, q.Y);
            float e = 1.0e-10f;
            return new float3(Hlsl.Abs(q.Z + (q.W - q.Y) / (6.0f * d + e)), d / (q.X + e), q.X);
        }

        private float3 LightWave(float3 input, bool isHSV, float2 uv)
        {
            float3 hsv = isHSV ? input : Rgb2Hsv(input);
            float2 p = -1.0f + 1.5f * uv.XY;
            float t = time / 5.0f;
            float x = p.X;
            float y = p.Y;

            float mov0 = x + y + Hlsl.Cos(Hlsl.Sin(t) * 2.0f) * 100.0f + Hlsl.Sin(x / 100.0f) * 1000.0f;
            float mov1 = y / 0.3f + t;
            float mov2 = x / 0.2f;

            float c1 = Hlsl.Sin(mov1 + t + randomValue1) / 2.0f + mov2 / 2.0f - mov1 - mov2 + t;
            float c2 = Hlsl.Cos(c1 + Hlsl.Sin(mov0 / 1000.0f + t - randomValue2) + Hlsl.Sin(y / 40.0f + t + randomValue3) + Hlsl.Sin((x + y) / 100.0f) * 3.0f);
            float c3 = Hlsl.Abs(Hlsl.Sin(c2 + Hlsl.Cos(mov1 + mov2 + c2) + Hlsl.Cos(mov2) + Hlsl.Sin(x / 1000.0f)));

            return Hsv2Rgb(new float3(
                Range(Hlsl.Abs(c2), hsv.X * 0.95f, hsv.X),
                Range(c3, hsv.Y, hsv.Y * 0.85f),
                Range(c3, hsv.Z, hsv.Z * 0.85f)));
        }

        private float RemapTri(float v)
        {
            float original = v * 2.0f - 1.0f;
            v = original / Hlsl.Sqrt(Hlsl.Abs(original));
            v = Hlsl.Max(-1.0f, v);
            v = v - Hlsl.Sign(original) + 0.5f;
            return v;
        }

        private float3 RemapTri(float3 c) => new float3(RemapTri(c.R), RemapTri(c.G), RemapTri(c.B));

        private float3 ScreenSpaceDither(float2 screenPosition, float timeValue)
        {
            float dotValue = Hlsl.Dot(new float2(131.0f, 312.0f), screenPosition.XY + timeValue);
            float3 dither = new float3(dotValue, dotValue, dotValue);
            dither.XYZ = Hlsl.Frac(dither.XYZ / new float3(103.0f, 71.0f, 97.0f));
            return RemapTri(dither.XYZ) / 32.0f;
        }

        public float4 Execute()
        {
            float2 scene = D2D.GetScenePosition().XY;
            float2 uv = scene / resolution;
            float2 tuv = uv - 0.5f;

            float degree = F_Noise(new float2(time * 0.1f, tuv.X * tuv.Y));
            tuv = Rotate(tuv, Hlsl.Radians(((degree - 0.5f) * 720.0f) + 180.0f));

            float speed = time * 0.75f;
            float3 dither = ScreenSpaceDither(scene, time);

            tuv.X += Hlsl.Sin((tuv.Y * 5.0f) + speed) / 25.0f;
            tuv.Y += Hlsl.Sin(((tuv.X * 5.0f) * 1.5f) + speed) / 12.5f;

            float rotatedX = Rotate(tuv, Hlsl.Radians(-5.0f)).X;
            float3 layer1 = Hlsl.Lerp(color1, color2, Hlsl.SmoothStep(-0.3f, 0.2f, rotatedX));
            float3 layer2 = Hlsl.Lerp(color3, color4, Hlsl.SmoothStep(-0.3f, 0.2f, rotatedX));
            float3 finalComp = Hlsl.Lerp(layer1, layer2, Hlsl.SmoothStep(0.5f, -0.3f, tuv.Y));

            return new float4(Hlsl.Saturate(finalComp + dither), 1.0f);
        }
    }
}
