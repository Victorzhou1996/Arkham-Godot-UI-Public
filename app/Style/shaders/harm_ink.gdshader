shader_type canvas_item;
uniform float pressure = 0.0;
uniform float bruise = 0.0;
varying vec4 inherited_color;
void vertex() { inherited_color = COLOR; }
float noise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453);
    float b = fract(sin(dot(cell + vec2(1.0, 0.0), vec2(127.1, 311.7))) * 43758.5453);
    float c = fract(sin(dot(cell + vec2(0.0, 1.0), vec2(127.1, 311.7))) * 43758.5453);
    float d = fract(sin(dot(cell + vec2(1.0), vec2(127.1, 311.7))) * 43758.5453);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
void fragment() {
    vec4 picture = texture(TEXTURE, UV);
    float grain = noise(UV * 18.0) * 0.14 + noise(UV * 47.0) * 0.045;
    float edge = max(abs(UV.x - 0.5) * 2.0, abs(UV.y - 0.5) * 1.85);
    float ink = smoothstep(1.06 - pressure * 0.72, 1.24 - pressure * 0.72, edge + grain);
    float gray = dot(picture.rgb, vec3(0.299, 0.587, 0.114));
    vec3 faded = mix(picture.rgb, vec3(gray) * vec3(0.70, 0.75, 0.86), pressure * 0.72);
    faded = mix(faded, vec3(0.025, 0.022, 0.041), ink * pressure * 0.94);
    faded = mix(faded, faded * vec3(0.44, 0.23, 0.18), bruise * 0.8);
    COLOR = vec4(faded, picture.a) * inherited_color;
}
