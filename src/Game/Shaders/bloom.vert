#version 450

// Bloom: fullscreen trojuhelnik BEZ vertex bufferu, stejny trik jako obloha.
//
// dvakrat, jeden protazeny trojuhelnik zadny sev nema.
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(location = 0) out vec2 vUv;

void main()
{
    // Indexy 0,1,2 dají body (-1,-1), (3,-1), (-1,3). Trojuhelnik je vetsi nez obrazovka
    // a jeji ctverec pokryje cely; co precniva, orizne rasterizer.
    vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

    vUv = corner;
    gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
}
