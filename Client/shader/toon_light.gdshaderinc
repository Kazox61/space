group_uniforms light;
uniform vec4 shadow_tint: source_color = vec4(1.0);
uniform sampler2D shadown_curve : repeat_disable, filter_linear_mipmap;
uniform float rim_light_size = 0.25;
uniform float rim_light_intensity = 0.0;

#include "fresnel.gdshaderinc"

void light() {
	float f = fresnel(1.0, NORMAL, VIEW);
	float d = texture(shadown_curve, vec2(dot(NORMAL, LIGHT) * ATTENUATION, 0.0)).x - (1.0-f) * 0.1;
	DIFFUSE_LIGHT = mix(vec3(1.0), mix(ALBEDO, shadow_tint.rgb, shadow_tint.a), d);
	if (rim_light_intensity > 0.0) {
		DIFFUSE_LIGHT += clamp(vec3(1.0) * step(1.0 - rim_light_size, f) * (1.0 - d), 0.0, 1.0) * rim_light_intensity;
	}
}