cbuffer Color : register(b0)
{
    float4 Coded;    // the layout: 0 4:2:0, 1 4:2:2 packed, 2 AYUV, 3 Y410 and Y416, 4 monochrome; the codes of a texture's 1, of the
                     // luma and of the chroma; 1 to look the values up in the table
    float4 Range;    // the luma's offset and scale, the chroma's
    float4 OfY;      // the three values' parts of the luma, of Cb below 0 and above, of Cr below 0 and above
    float4 CbBelow;
    float4 CbAbove;
    float4 CrBelow;
    float4 CrAbove;
    float4 Table;    // its steps each way
    float4 Turn;     // the matrix the picture is shown of, undone: of a pixel shown, where it is in the picture, A x + B y and C x + D y
    float4 TurnOffset;
};
Texture2D<float4> Plane0 : register(t0);
Texture2D<float4> Plane1 : register(t1);
Texture3D<float4> Lut : register(t2);
SamplerState Linear : register(s0);

struct Vertex { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

Vertex VS(uint id : SV_VertexID)
{
    Vertex v;
    v.Uv = float2((id << 1) & 2, id & 2);
    v.Position = float4(v.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float4 PS(Vertex input) : SV_Target
{
    int2 shown = int2(input.Position.xy);
    int2 p = int2((int)Turn.x * shown.x + (int)Turn.y * shown.y + (int)TurnOffset.x, (int)Turn.z * shown.x + (int)Turn.w * shown.y + (int)TurnOffset.y);
    int layout = (int)Coded.x;
    float3 c;    // Y, Cb and Cr, each of a texture's 0 to 1
    bool monochrome = false;
    if (layout == 0)
    {
        c.x = Plane0.Load(int3(p, 0)).r;
        c.yz = Plane1.Load(int3(p / 2, 0)).rg;
    }
    else if (layout == 1)
    {
        // of each two pixels Y0, Cb, Y1 and Cr
        float4 q = Plane0.Load(int3(p.x / 2, p.y, 0));
        c = float3((p.x & 1) != 0 ? q.b : q.r, q.g, q.a);
    }
    else if (layout == 2)
    {
        // Cr, Cb, Y, alpha
        float4 q = Plane0.Load(int3(p, 0));
        c = float3(q.b, q.g, q.r);
    }
    else if (layout == 3)
    {
        // Cb, Y, Cr, alpha
        float4 q = Plane0.Load(int3(p, 0));
        c = float3(q.g, q.r, q.b);
    }
    else
    {
        c = float3(Plane0.Load(int3(p, 0)).r, 0, 0);
        monochrome = true;
    }

    float y = (c.x * Coded.y - Range.x) * Range.y;
    float cb = monochrome ? 0 : (c.y * Coded.z - Range.z) * Range.w;
    float cr = monochrome ? 0 : (c.z * Coded.z - Range.z) * Range.w;
    float3 t = OfY.xyz * y + (cb < 0 ? CbBelow.xyz : CbAbove.xyz) * cb + (cr < 0 ? CrBelow.xyz : CrAbove.xyz) * cr;
    if (Coded.w > 0.5)
    {
        // between the steps, of the steps' centres
        float n = Table.x;
        return float4(Lut.SampleLevel(Linear, saturate(t) * ((n - 1) / n) + 0.5 / n, 0).rgb, 1);
    }
    return float4(saturate(t), 1);
}