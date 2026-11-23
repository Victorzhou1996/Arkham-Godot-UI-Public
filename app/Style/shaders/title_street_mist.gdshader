shader_type canvas_item;
render_mode unshaded;
uniform float seconds = 0.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(cell), hash(cell + vec2(1.0, 0.0)), f.x),
		mix(hash(cell + vec2(0.0, 1.0)), hash(cell + vec2(1.0, 1.0)), f.x), f.y);
}

float wisp(vec2 uv, float offset, float lane) {
	// The wisp fades out before its position resets, always flowing one way.
	float age = fract(seconds / 16.0 + offset);
	vec2 center = vec2(-0.20 + age * 1.4, lane);
	vec2 flow = vec2(uv.x * 7.0 - age * 3.0, uv.y * 4.0 + offset * 9.0);
	vec2 billow = vec2(noise(flow * 0.6), noise(flow * 0.8 + vec2(7.3, 2.1))) - vec2(0.5);
	vec2 q = (uv - center + billow * vec2(0.06, 0.10)) / vec2(0.37, 0.26);
	float edge = 1.0 - smoothstep(0.12, 1.0, dot(q, q));
	float grain = noise(flow) * 0.5 + noise(flow * 2.0 + vec2(5.2, 3.6)) * 0.3 + noise(flow * 4.0) * 0.2;
	float life = smoothstep(0.0, 0.12, age) * (1.0 - smoothstep(0.88, 1.0, age));
	return edge * (0.35 + grain * 0.65) * life;
}

void fragment() {
	float density = wisp(UV, 0.28, 0.55) + wisp(UV, 0.75, 0.83) * 0.8;
	float boundary = smoothstep(0.0, 0.08, UV.x) * (1.0 - smoothstep(0.91, 1.0, UV.x))
		* smoothstep(0.0, 0.10, UV.y) * (1.0 - smoothstep(0.90, 1.0, UV.y));
	COLOR = vec4(0.65, 0.73, 0.75, min(0.36, density * boundary * 0.42));
}
