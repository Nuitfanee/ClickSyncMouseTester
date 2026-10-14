cbuffer Transform : register(b0) {
    float2 scale;
    float2 offset;
    float2 extent;
    float radius;
    float half_width;
    float4 color;
    float4 clip_rect;
    float4 reserved;
};

struct Instance {
    float2 a : POSITION0;
    float2 b : POSITION1;
    uint kind : KIND0;
};

struct Pixel {
    float4 position : SV_Position;
    nointerpolation float2 a : A;
    nointerpolation float2 b : B;
    nointerpolation uint kind : KIND;
};

Pixel chart_vs(Instance item, uint vertex : SV_VertexID) {
    static const float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1)
    };
    Pixel output;
    output.a = item.a * scale + offset;
    output.b = item.b * scale + offset;
    output.kind = item.kind;
    if (reserved.x == 1 && item.kind == 2) {
        // Separate histogram bins in screen space without changing their data edges.
        float gap = min(abs(output.b.x - output.a.x) * 0.18, 10.0);
        output.a.x += gap * 0.5;
        output.b.x -= gap * 0.5;
    }
    float2 minimum = min(output.a, output.b);
    float2 maximum = max(output.a, output.b);
    float margin = item.kind == 0 ? radius + 1 : half_width + 1;
    if (item.kind != 2) {
        minimum -= margin;
        maximum += margin;
    }
    // Bound rasterization to the plot even for long segments outside the view.
    minimum = max(minimum, clip_rect.xy);
    maximum = min(maximum, clip_rect.zw);
    float2 location = lerp(minimum, max(minimum, maximum), corners[vertex]);
    if (item.kind == 1 || item.kind == 3 || item.kind == 4) {
        // A thin oriented quad avoids shading the entire bounding box of a diagonal.
        float2 delta = output.b - output.a;
        float segment_length = length(delta);
        float2 tangent = segment_length > 0 ? delta / segment_length : float2(1, 0);
        float2 normal = float2(-tangent.y, tangent.x);
        float2 corner = corners[vertex];
        location = output.a + tangent * lerp(-margin, segment_length + margin, corner.x)
            + normal * lerp(-margin, margin, corner.y);
    }
    output.position = float4(location.x * 2 / extent.x - 1, 1 - location.y * 2 / extent.y, 0, 1);
    return output;
}

float4 chart_ps(Pixel item) : SV_Target {
    float2 location = item.position.xy;
    if (any(location < clip_rect.xy) || any(location >= clip_rect.zw)) discard;
    float coverage = 1;
    if (item.kind != 2) {
        float2 delta = item.b - item.a;
        float length_squared = dot(delta, delta);
        float t = length_squared > 0 ? saturate(dot(location - item.a, delta) / length_squared) : 0;
        float distance = length(location - (item.a + t * delta));
        float width = item.kind == 0 ? radius : half_width;
        if (reserved.x == 1 && item.kind == 3) width *= 0.7;
        coverage = saturate(width + 0.5 - distance);
    }
    // GPUI consumes premultiplied BGRA8 layers.
    float4 ink = color;
    if (reserved.x == 1 && item.kind == 3) ink = float4(reserved.yzw, 0.16);
    if (reserved.x == 1 && item.kind == 4) ink = float4(reserved.yzw, 0.58);
    float alpha = ink.a * coverage;
    return float4(ink.rgb * alpha, alpha);
}
