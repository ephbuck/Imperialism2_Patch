/* Stage 0 observation only. The launcher must verify the complete EXE hash.
 * Frida Interceptor changes process code to observe calls, but this script never
 * replaces arguments/results, writes game data, changes geometry, or calls Draw.
 * Addresses/signatures: docs/evidence/high-resolution/rea-*.json.
 */
(function () {
    'use strict';
    const expectedHash = '998384bb696f02d37a6725cea33f98bc2ca711b803c2638bed07a91d687ca7cc';
    const listeners = [];
    const counts = Object.create(null);
    const samples = Object.create(null);
    let stopped = false;
    let timer = null;
    let lastWindow = Date.now();
    let activeMap = null;
    function emit(kind, fields) {
        send(Object.assign({ schema: 1, probe: 'imp2-rendering', kind: kind, timestamp: Date.now() }, fields));
    }
    function sample(kind) {
        counts[kind] = (counts[kind] || 0) + 1;
        const now = Date.now();
        if (now - lastWindow >= 1000) {
            Object.keys(samples).forEach(k => { delete samples[k]; });
            lastWindow = now;
        }
        samples[kind] = (samples[kind] || 0) + 1;
        return samples[kind] <= 4;
    }
    function safe(read) { try { return read(); } catch (_) { return null; } }
    function address(value) { return value ? value.toString() : null; }
    function point(p) { return safe(() => ({ x: p.readS32(), y: p.add(4).readS32() })); }
    function rect(p) {
        return safe(() => ({ left: p.readS32(), top: p.add(4).readS32(), right: p.add(8).readS32(), bottom: p.add(12).readS32() }));
    }
    function pointerAt(p) { return safe(() => address(p.readPointer())); }
    function mapState(p) {
        return safe(() => ({ object: address(p), vtable: pointerAt(p),
            origin: point(p.add(0x24)), size: point(p.add(0x34)),
            camera: point(p.add(0x64)), cacheWorld: pointerAt(p.add(0x3f8)) }));
    }
    function cacheState(p) {
        return safe(() => {
            let pinned = 0;
            const owners = [];
            for (let i = 0; i < 110; ++i) {
                const entry = p.add(0x84 + i * 8);
                if (entry.readU8() !== 0) ++pinned;
                if (i < 4) owners.push({ slot: i, pinned: entry.readU8(), row: entry.add(2).readS16(), column: entry.add(4).readS16(), tile: entry.add(6).readS16() });
            }
            return { pinned: pinned, capacity: 110, firstOwners: owners };
        });
    }
    function protect(callback) {
        return function () {
            if (stopped) return;
            try { return callback.apply(this, arguments); }
            catch (error) { if (sample('probe-error')) emit('probe-error', { error: String(error) }); }
        };
    }
    function attach(target, callbacks) {
        const wrapped = {};
        if (callbacks.onEnter) wrapped.onEnter = protect(callbacks.onEnter);
        if (callbacks.onLeave) wrapped.onLeave = protect(callbacks.onLeave);
        listeners.push(Interceptor.attach(target, wrapped));
    }
    function stop() {
        if (stopped) return;
        stopped = true;
        if (timer !== null) clearInterval(timer);
        listeners.forEach(listener => listener.detach());
        emit('stopped', { counts: Object.assign({}, counts) });
    }
    const hooks = [
        ['map-draw', 0x55d070, '64 a1 00 00 00 00 6a ff'],
        ['cache-slot', 0x55d3e3, '0f bf c2 66 8b 9c c6 8a'],
        ['screen-to-tile', 0x55a5a0, '53 56 57 8b 7c 24 10 8b'],
        ['camera-set', 0x55aa30, 'a1 f4 06 76 00 83 ec 10'],
        ['minimap-track', 0x60f150, '8b 44 24 04 53 56 57 85'],
        ['minimap-size', 0x60ecd0, '8b 44 24 04 8b 10 89 91'],
        ['copy-bits', 0x4aaa40, '64 a1 00 00 00 00 6a ff'],
        ['display-request', 0x48ad90, '81 ec 80 00 00 00 53 55'],
        ['surface-create', 0x48afa0, '81 ec 80 00 00 00 53 56'],
        ['focus-idle', 0x4b86e0, '64 a1 00 00 00 00 6a ff'],
        ['transparent-focus-idle', 0x4b8c10, '6a ff 68 f8 3c 6d 00 64'],
        ['view-invalidate', 0x49fbf0, '83 ec 10 56 8b f1 8b 46']
    ];
    try {
        if (Process.arch !== 'ia32' || Process.pointerSize !== 4 || Process.platform !== 'windows') throw new Error('Expected Windows x86 process');
        if (!Process.mainModule.base.equals(ptr(0x400000))) throw new Error('Unsupported image base');
        // Validate all code before installing any hook; do not accept a partial profile.
        hooks.forEach(hook => {
            const expected = hook[2].split(' ').map(b => parseInt(b, 16));
            const actual = new Uint8Array(ptr(hook[1]).readByteArray(expected.length));
            if (actual.some((b, i) => b !== expected[i])) throw new Error('Signature mismatch: ' + hook[0]);
        });
        const native = Object.create(null);
        hooks.forEach(h => { native[h[0]] = ptr(h[1]); });
        attach(native['map-draw'], {
            onEnter(args) {
                activeMap = this.context.ecx;
                this.observed = sample('map-draw');
                if (!this.observed) return;
                this.started = Date.now();
                this.map = activeMap;
                emit('map-draw', { map: mapState(this.map), rectangle: rect(args[0]), cache: cacheState(this.map),
                    tacticalWorld: pointerAt(ptr(0x75c0e8)), screenPort: pointerAt(ptr(0x759f90)) });
            },
            onLeave() { if (this.observed) emit('map-draw-return', { elapsedMs: Date.now() - this.started, map: mapState(this.map) }); }
        });
        attach(native['cache-slot'], {
            onEnter() {
                const raw = this.context.edx.toInt32() & 0xffff;
                const slot = raw >= 0x8000 ? raw - 0x10000 : raw;
                counts['cache-slot'] = (counts['cache-slot'] || 0) + 1;
                if ((slot < 0 || slot >= 110) && sample('cache-out-of-range'))
                    emit('cache-out-of-range', { slot: slot, capacity: 110, map: mapState(this.context.esi),
                        action: 'observed-only-no-intervention' });
            }
        });
        attach(native['screen-to-tile'], {
            onEnter(args) {
                this.observed = sample('screen-to-tile');
                if (!this.observed) return;
                this.map = this.context.ecx;
                this.input = point(args[0]);
                this.outputs = [args[1], args[2], args[3]];
            },
            onLeave() { if (this.observed) emit('screen-to-tile', { map: mapState(this.map), local: this.input,
                column: safe(() => this.outputs[0].readS16()), row: safe(() => this.outputs[1].readS16()),
                subregion: safe(() => this.outputs[2].readS16()) }); }
        });
        attach(native['camera-set'], {
            onEnter(args) {
                this.observed = sample('camera-set'); this.map = this.context.ecx;
                if (this.observed) emit('camera-set', { map: mapState(this.map), requestedColumn: args[0].toInt32(), requestedRow: args[1].toInt32() });
            },
            onLeave() { if (this.observed) emit('camera-set-return', { map: mapState(this.map) }); }
        });
        ['minimap-track', 'minimap-size'].forEach(kind => attach(native[kind], {
            onEnter(args) {
                if (!sample(kind)) return;
                const p = this.context.ecx;
                emit(kind, { object: address(p), vtable: pointerAt(p), owner: pointerAt(p.add(0x88)),
                    origin: point(p.add(0x8c)), extent: point(p.add(0x9c)),
                    argument0: address(args[0]), sizeArgument: kind === 'minimap-size' ? point(args[0]) : null,
                    // TrackMouse point is the fourth stack parameter (verified at +0x1c after three pushes).
                    localPoint: kind === 'minimap-track' ? point(args[3]) : null });
            }
        }));
        attach(native['copy-bits'], {
            onEnter(args) {
                const finalMapCopy = this.returnAddress.equals(ptr(0x55d88e));
                const kind = finalMapCopy ? 'map-final-copy' : 'copy-bits';
                if (!sample(kind)) return;
                emit(kind, { caller: address(this.returnAddress), sourceHandle: address(args[0]), destinationHandle: address(args[1]),
                    sourcePixmap: pointerAt(args[0]), destinationPixmap: pointerAt(args[1]), source: rect(args[2]), destination: rect(args[3]) });
            }
        });
        attach(native['display-request'], {
            onEnter(args) {
                if (sample('display-request')) emit('display-request', { object: address(this.context.ecx), width: args[0].toInt32(), height: args[1].toInt32(), bitsPerPixel: args[2].toInt32() });
            }
        });
        ['surface-create', 'focus-idle', 'transparent-focus-idle', 'view-invalidate'].forEach(kind => attach(native[kind], {
            onEnter(args) {
                if (!sample(kind)) return;
                emit(kind, { object: address(this.context.ecx), caller: address(this.returnAddress),
                    rectangle: kind === 'view-invalidate' ? rect(args[0]) : null,
                    owner: kind.indexOf('focus') >= 0 ? pointerAt(this.context.ecx.add(4)) : null });
            }
        }));
        // Observe only APIs already resolved by this executable's IAT. No export guessing.
        const apiHooks = [
            ['gdi-bitblt', 0x76bdec], ['gdi-stretchdibits', 0x76bde0],
            ['clip-intersect', 0x76bedc], ['clip-select-region', 0x76bed4],
            ['clip-ext-select-region', 0x76bf1c], ['window-client-rect', 0x76c5d8]
        ];
        apiHooks.forEach(hook => {
            const target = ptr(hook[1]).readPointer();
            if (target.isNull()) throw new Error('Unresolved IAT: ' + hook[0]);
            attach(target, {
                onEnter(args) {
                    this.observed = sample(hook[0]);
                    if (!this.observed) return;
                    this.output = args[1]; this.handle = address(args[0]);
                    const fields = { handle: this.handle, caller: address(this.returnAddress) };
                    if (hook[0] === 'gdi-bitblt' || hook[0] === 'gdi-stretchdibits') {
                        fields.destination = { x: args[1].toInt32(), y: args[2].toInt32(), width: args[3].toInt32(), height: args[4].toInt32() };
                        if (hook[0] === 'gdi-bitblt') fields.sourceDC = address(args[5]);
                    } else if (hook[0] === 'clip-intersect') {
                        fields.rectangle = { left: args[1].toInt32(), top: args[2].toInt32(), right: args[3].toInt32(), bottom: args[4].toInt32() };
                    } else if (hook[0].indexOf('region') >= 0) fields.region = address(args[1]);
                    if (hook[0] !== 'window-client-rect') emit(hook[0], fields);
                },
                onLeave(result) {
                    if (this.observed && hook[0] === 'window-client-rect') emit(hook[0], { handle: this.handle, success: result.toInt32() !== 0, rectangle: result.toInt32() !== 0 ? rect(this.output) : null });
                }
            });
        });
        timer = setInterval(() => emit('summary', { counts: Object.assign({}, counts), activeMap: address(activeMap) }), 1000);
        rpc.exports = { snapshot: function () { return { counts: Object.assign({}, counts), activeMap: address(activeMap) }; }, stop: stop };
        emit('ready', { expectedExecutableSha256: expectedHash, executable: Process.mainModule.path,
            modules: Process.enumerateModules().map(m => ({ name: m.name, path: m.path, base: address(m.base) })),
            nativeHooks: hooks.length, apiHooks: apiHooks.length, sampleLimitPerKindPerSecond: 4 });
    } catch (error) {
        stop();
        emit('refused', { error: String(error) });
        throw error;
    }
}());
