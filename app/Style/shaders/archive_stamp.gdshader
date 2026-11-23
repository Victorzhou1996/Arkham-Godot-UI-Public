shader_type canvas_item;
// Missing ink lets the original paper show through every part of the seal.
varying vec2 ink_position;

void vertex() {
	ink_position = VERTEX;
}

float grain(vec2 point) {
	return fract(sin(dot(point, vec2(127.1, 311.7))) * 43758.5453);
}

void fragment() {
	float pores = smoothstep(0.02, 0.08, grain(floor(ink_position * 1.35)));
	float pressure = 0.9 + 0.1 * grain(floor(ink_position * 0.14));
	float scratch = 1.0 - 0.2 * step(0.97, grain(vec2(floor(ink_position.y * 0.8), 5.0)));
	COLOR.a *= pores * pressure * scratch;
}
