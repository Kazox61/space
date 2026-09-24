"""Export the generated locomotion rig from huntress_locomotion.blend.

Run with Blender MCP or --background <blend> --python <this script>.
Exports the production rig with idle/run/jump/fall/land/tilt, bow_hold, and
attack clips. Run promote_huntress_weapon_actions.py before exporting if the
runtime arm-parent settings differ from the source weapon rig.
"""

from pathlib import Path

import bpy


def export():
    if bpy.context.mode != 'OBJECT':
        raise RuntimeError('Switch to Object mode before exporting.')
    rig = bpy.data.objects['hunter_locomotion']
    mesh = bpy.data.objects['HunterLocomotionModel']
    source_rig = bpy.data.objects['hunter']
    for bone_name in ('ArmProperties.L', 'ArmProperties.R'):
        source_bone = source_rig.pose.bones[bone_name]
        runtime_bone = rig.pose.bones[bone_name]
        for property_name in ('World Elbow ', 'Local IK', 'Hip Parent'):
            if runtime_bone[property_name] != source_bone[property_name]:
                raise RuntimeError(
                    f'{bone_name}[{property_name!r}] differs from the source weapon rig; '
                    'run promote_huntress_weapon_actions.py before exporting.'
                )
    root = Path(bpy.data.filepath).resolve().parents[2]
    output = root / 'Client/assets/player/model/huntress_locomotion.glb'
    if not output.parent.is_dir():
        raise RuntimeError('Save the working blend under blender/models first.')
    selected = list(bpy.context.selected_objects)
    active = bpy.context.view_layer.objects.active
    animation = rig.animation_data
    previous_action, previous_slot = animation.action, animation.action_slot
    previous_frame = bpy.context.scene.frame_current
    tracks = []
    weapon_mask = mesh.modifiers['Hide weapon for neutral locomotion']
    previous_mask_viewport = weapon_mask.show_viewport
    previous_mask_render = weapon_mask.show_render
    try:
        weapon_mask.show_viewport = False
        weapon_mask.show_render = False
        animation.action = None
        bpy.ops.object.select_all(action='DESELECT')
        rig.select_set(True)
        mesh.select_set(True)
        bpy.context.view_layer.objects.active = rig
        # Explicit action membership keeps review/source clips out of the runtime file.
        for name in ('idle', 'run', 'jump', 'fall', 'land', 'tilt_l', 'tilt_r',
                     'bow_hold', 'attack'):
            track = rig.animation_data.nla_tracks.new()
            tracks.append(track)
            track.name = name
            action = bpy.data.actions[name]
            strip = track.strips.new(name, 1, action)
            strip.action_slot = action.slots[0]
            track.mute = True
        bpy.ops.export_scene.gltf(
            filepath=str(output), check_existing=False, export_format='GLB',
            use_selection=True, export_apply=True, export_skins=True,
            export_influence_nb=8,
            export_animations=True, export_animation_mode='ACTIONS',
            export_anim_single_armature=False, export_force_sampling=True,
            export_bake_animation=True, export_frame_range=False,
            export_frame_step=1, export_anim_slide_to_zero=True,
            export_def_bones=True, export_reset_pose_bones=True,
            export_optimize_animation_size=True, export_morph=False,
            export_cameras=False, export_lights=False, export_materials='EXPORT',
        )
    finally:
        weapon_mask.show_viewport = previous_mask_viewport
        weapon_mask.show_render = previous_mask_render
        for track in tracks:
            rig.animation_data.nla_tracks.remove(track)
        animation.action = previous_action
        if previous_action:
            animation.action_slot = previous_slot
        bpy.context.scene.frame_set(previous_frame)
        bpy.ops.object.select_all(action='DESELECT')
        for obj in selected:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = active
    return str(output)


EXPORT_RESULT = export()
