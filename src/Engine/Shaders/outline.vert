#version 450

// Obrys tvaru, na ktery hrac miri. Vrchol nese jen pozici ve svete; barvu ma cela davka
// spolecnou, takze se veze v push konstantach vedle matice.

layout(location = 0) in vec3 aPosition;

layout(push_constant) uniform Push
{
    mat4 viewProjection;  // 0..63
    vec4 color;           // 64..79
} pc;

void main()
{
    gl_Position = pc.viewProjection * vec4(aPosition, 1.0);
}
