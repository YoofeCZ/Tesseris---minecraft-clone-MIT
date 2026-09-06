#version 450

// Font je jednobitovy, takze se prazdne texely zahazuji misto michani - pruhlednost
// by stejne nabyvala jen krajnich hodnot.

layout(location = 0) in vec2 vTexCoord;

layout(set = 0, binding = 0) uniform sampler2D uFont;

layout(push_constant) uniform Push
{
    mat4 projection;
    vec4 color;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    if (texture(uFont, vTexCoord).r < 0.5)
    {
        discard;
    }

    FragColor = vec4(pc.color.rgb, 1.0);
}
