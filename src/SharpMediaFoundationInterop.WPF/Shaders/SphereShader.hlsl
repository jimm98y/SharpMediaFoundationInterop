cbuffer View : register(b0)
{
    float4 LeftEye;   // x, y, width, height of the left eye's picture, as fractions of the frame
    float4 RightEye;
    float4 Bounds;    // left, top, right, bottom of the whole sphere's picture cropped
    float4 Angles;    // yaw, pitch, tan of half the field of view, 1 for both eyes' views side by side
    float4 Output;    // the eye of a view of one, 1 the right; the output's width and height
    float4 Texel;     // the size of a pixel of the frame, as fractions of it
};
Texture2D Frame : register(t0);
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
    float2 uv = input.Uv;
    float right = Output.x;
    float width = Output.y;
    if (Angles.w > 0.5)
    {
        right = uv.x >= 0.5 ? 1 : 0;
        uv.x = frac(uv.x * 2);
        width *= 0.5;
    }
    float x = (uv.x * 2 - 1) * Angles.z;
    float y = (1 - uv.y * 2) * Angles.z * Output.z / width;

    // looking up by the pitch, then turned right by the yaw
    float cp = cos(Angles.y), sp = sin(Angles.y), cy = cos(Angles.x), sy = sin(Angles.x);
    float y1 = y * cp + sp;
    float z1 = -y * sp + cp;
    float x2 = x * cy + z1 * sy;
    float z2 = -x * sy + z1 * cy;
    float longitude = atan2(x2, z2);
    float latitude = atan2(y1, sqrt(x2 * x2 + z2 * z2));

    // of the whole sphere's picture, then of the part the eye's picture is of
    float u = (longitude / 6.28318530718 + 0.5 - Bounds.x) / (1 - Bounds.x - Bounds.z);
    float v = (0.5 - latitude / 3.14159265359 - Bounds.y) / (1 - Bounds.y - Bounds.w);
    if (u < 0 || u > 1 || v < 0 || v > 1)
        return float4(0, 0, 0, 1);

    // not across into the other eye's picture
    float4 eye = right > 0.5 ? RightEye : LeftEye;
    float2 p = clamp(eye.xy + float2(u, v) * eye.zw, eye.xy + Texel.xy * 0.5, eye.xy + eye.zw - Texel.xy * 0.5);
    return float4(Frame.SampleLevel(Linear, p, 0).rgb, 1);
}