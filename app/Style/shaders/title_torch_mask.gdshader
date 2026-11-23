shader_type canvas_item;
render_mode unshaded;

void fragment() {
	vec4 ink = texture(TEXTURE, UV);
	// Remove the baked flame only. The black torch head starts at source y=158.
	// Pose articulation is handled by the mesh; keep the painted colours intact.
	float old_flame = (1.0 - step(158.0 / 941.0, UV.y))
		* step(1340.0 / 1671.0, UV.x) * (1.0 - step(1510.0 / 1671.0, UV.x));
	COLOR = vec4(ink.rgb, ink.a * (1.0 - old_flame));
}
