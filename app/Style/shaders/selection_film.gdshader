shader_type canvas_item;
render_mode unshaded;

uniform float seconds = 0.0;
uniform float close_amount = 0.0;
uniform bool text_left = false;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec2 p = UV;
	float edges = smoothstep(0.28, 0.82, length((p - vec2(0.43, 0.46)) * vec2(1.1, 0.85)));
	float right = smoothstep(0.47, 0.90, text_left ? 1.0 - p.x : p.x) * close_amount;
	float floor_shade = smoothstep(0.57, 1.0, p.y);
	float opacity = 1.0 - (1.0 - edges * 0.48) * (1.0 - right * 0.76) * (1.0 - floor_shade * 0.52);
	float grain = (hash(floor(p * vec2(1440.0, 810.0)) + floor(seconds * 12.0)) - 0.5) * 0.025;
	COLOR = vec4(vec3(0.007, 0.015, 0.021) + grain, clamp(opacity, 0.0, 0.95));
}
