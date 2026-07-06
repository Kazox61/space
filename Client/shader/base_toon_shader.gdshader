shader_type spatial;
render_mode ambient_light_disabled, cull_back;

uniform sampler2D albedo_sampler : source_color, filter_linear, repeat_enable;
uniform vec2 uv_offset = vec2(0.0);
uniform vec2 uv_scale = vec2(1.0);

uniform vec4 emission_color : source_color = vec4(0.0);
uniform float emission_intensity = 0.0;

#include "toon_light.gdshaderinc"

void fragment() {
	vec4 t = texture(albedo_sampler, UV * uv_scale + uv_offset);
	ALBEDO = mix(t.rgb, emission_color.rgb, emission_color.a);
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	if (emission_intensity > 0.0) {
		EMISSION = emission_color.rgb * emission_intensity;
	}
}