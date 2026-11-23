shader_type canvas_item;
render_mode unshaded;

uniform float seconds = 0.0;
varying vec4 tint;

void vertex() {
	tint = COLOR;
}

float hash(vec2 p) {
	// A periodic vertical lattice gives exactly the same density after 8 seconds.
	p.y = mod(p.y, 32.0);
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(cell), hash(cell + vec2(1.0, 0.0)), f.x),
		mix(hash(cell + vec2(0.0, 1.0)), hash(cell + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	vec4 paint = texture(TEXTURE, UV);
	// Move combustion up through the paint; never displace the image or its root.
	vec2 flow = UV * vec2(12.0, 15.0) + vec2(0.0, seconds * 4.0);
	float density = noise(flow) * 0.65 + noise(flow * 2.0 + vec2(3.4, 17.2)) * 0.35;
	float height = clamp((1107.0 / 1254.0 - UV.y) / 0.76, 0.0, 1.0);
	float edge = 1.0 - smoothstep(0.15, 0.85, paint.a);
	float threshold = -0.20 + pow(height, 1.4) * 0.70 + edge * 0.06;
	float burn = smoothstep(threshold, threshold + 0.20, density);
	COLOR = vec4(paint.rgb * (0.97 + density * 0.06), paint.a * burn) * tint;
}
