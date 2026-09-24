"""Create the runtime bow-hold pose from the preserved source animation."""

import bpy


def promote():
    source_rig = bpy.data.objects['hunter']
    runtime_rig = bpy.data.objects['hunter_locomotion']
    for bone_name in ('ArmProperties.L', 'ArmProperties.R'):
        source_bone = source_rig.pose.bones[bone_name]
        runtime_bone = runtime_rig.pose.bones[bone_name]
        for property_name in ('World Elbow ', 'Local IK', 'Hip Parent'):
            runtime_bone[property_name] = source_bone[property_name]

    existing = bpy.data.actions.get('bow_hold')
    if existing:
        if existing.get('source_action') != 'source_idle@1':
            raise RuntimeError('Existing bow_hold was not generated from source_idle@1.')
        return {'action': existing.name, 'frames': list(existing.frame_range), 'created': False}

    hold = bpy.data.actions['source_idle'].copy()
    hold.name = 'bow_hold'
    hold.use_fake_user = True
    hold.use_frame_range = True
    hold.use_cyclic = True
    hold.frame_start, hold.frame_end = 1, 2
    hold['source_action'] = 'source_idle@1'
    hold['review_status'] = 'Runtime bow-hold pose'

    for layer in hold.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                values = [(curve, curve.evaluate(1)) for curve in bag.fcurves]
                for curve, value in values:
                    curve.keyframe_points.clear()
                    curve.keyframe_points.insert(1, value)
                    curve.keyframe_points.insert(2, value)
                    curve.update()

    return {'action': hold.name, 'frames': list(hold.frame_range), 'created': True}


if __name__ == '__main__':
    PROMOTION_RESULT = promote()
