import test from 'node:test';
import assert from 'node:assert/strict';
import { validateListDesignOptions, validateDesignOptionsManifest, validateNamedViews, validateGlbAgainstManifest } from './verify-design-options.mjs';

const manifest = () => ({
    sets: [{ setKey: 'S1', setName: '整體外觀', options: [
        { optionKey: 'O1', optionName: 'A', isPrimary: true, elementCount: 5 },
        { optionKey: 'O2', optionName: 'B', isPrimary: false, elementCount: 3 }] }],
    generatedAt: '2026-10-24T00:00:00.000Z', revitDoc: 'AR_CPC'
});
const view = (over = {}) => ({
    viewId: 1, uniqueId: 'u1', name: '透視 1', isPerspective: true, displayStyle: 'Shaded', viewerMode: 'shaded',
    gltf: { eye: [0, 1.6, 0], target: [0, 1.6, -10], up: [0, 1, 0], fovVerticalDeg: null, orthoWidthMeters: null, orthoHeightMeters: null },
    sectionBox: { enabled: false, min: null, max: null }, revit: { eye: [0, 0, 1.6] }, ...over
});

test('manifest 合約：缺 revitDoc 或選項數為 0 要拒絕', () => {
    assert.equal(validateDesignOptionsManifest(manifest()), true);
    assert.throws(() => validateDesignOptionsManifest({ ...manifest(), revitDoc: '' }));
    const m = manifest(); m.sets[0].options[1].elementCount = 0;
    assert.throws(() => validateDesignOptionsManifest(m), /elementCount/);
});

test('list_design_options 合約：主選項必須剛好 1 個，空選項只回報不丟錯', () => {
    const data = { sets: manifest().sets.map(s => ({ ...s, options: s.options.map(o => ({ ...o })) })) };
    assert.deepEqual(validateListDesignOptions(data), { sets: 1, options: 2, emptyOptions: [] });
    data.sets[0].options[1].elementCount = 0;
    assert.equal(validateListDesignOptions(data).emptyOptions.length, 1);
    data.sets[0].options[1].isPrimary = true;
    assert.throws(() => validateListDesignOptions(data), /主選項/);
});

test('named_views 合約：座標有限、target 距離 10 m、sectionBox 啟用需 min/max', () => {
    const ok = { originOffsetMeters: [0, 0, 0], views: [view()], warnings: [] };
    assert.deepEqual(validateNamedViews(ok), { views: 1, perspective: 1 });
    assert.throws(() => validateNamedViews({ ...ok, originOffsetMeters: [0, NaN, 0] }));
    assert.throws(() => validateNamedViews({ ...ok, views: [view({ gltf: { ...view().gltf, target: [0, 1.6, -5] } })] }), /10 m/);
    assert.throws(() => validateNamedViews({ ...ok, views: [view({ sectionBox: { enabled: true, min: null, max: null } })] }), /sectionBox/);
    assert.throws(() => validateNamedViews({ ...ok, views: [view(), view()] }), /重複/);
    assert.throws(() => validateNamedViews({ ...ok, views: [view({ viewerMode: 'bogus' })] }), /viewerMode/);
});

test('GLB designOption 的 setKey/optionKey 必須都在 design_options.json', () => {
    const glb = keys => ({ nodes: [{ extras: { designOption: keys } }, { extras: {} }] });
    assert.equal(validateGlbAgainstManifest(glb({ setKey: 'S1', optionKey: 'O2' }), manifest()), true);
    assert.throws(() => validateGlbAgainstManifest(glb({ setKey: 'SX', optionKey: 'O2' }), manifest()), /setKey/);
    assert.throws(() => validateGlbAgainstManifest(glb({ setKey: 'S1', optionKey: 'OX' }), manifest()), /optionKey/);
});
