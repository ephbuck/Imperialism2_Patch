'use strict';
// Controlled Frida fixture: validates observation contracts, not native ABI/gameplay.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/graphics/observe-rendering.js'), 'utf8');
const signatures = [
    [0x55d070, '64a1000000006aff'], [0x55a5a0, '5356578b7c24108b'],
    [0x55aa30, 'a1f406760083ec10'], [0x60f150, '8b44240453565785'],
    [0x60ecd0, '8b4424048b108991'], [0x4aaa40, '64a1000000006aff'],
    [0x48ad90, '81ec800000005355'], [0x48afa0, '81ec800000005356'],
    [0x4b86e0, '64a1000000006aff'], [0x4b8c10, '6aff68f83c6d0064'],
    [0x49fbf0, '83ec10568bf18b46']
    , [0x55d3e3, '0fbfc2668b9cc68a']
];
function fixture(options = {}) {
    const memory = Buffer.alloc(0x900000);
    const events = [];
    const hooks = new Map();
    const detached = [];
    let timer;
    const read = (p, size, method) => { if (p.value === 0) throw new Error('null pointer'); return memory[method](p.value); };
    class Pointer {
        constructor(value) { this.value = typeof value === 'string' ? parseInt(value, 16) : value; }
        add(n) { return new Pointer(this.value + n); }
        equals(other) { return this.value === other.value; }
        isNull() { return this.value === 0; }
        toString() { return '0x' + this.value.toString(16); }
        toInt32() { return this.value | 0; }
        readByteArray(size) { return Uint8Array.from(memory.subarray(this.value, this.value + size)).buffer; }
        readPointer() { return new Pointer(read(this, 4, 'readUInt32LE')); }
        readS32() { return read(this, 4, 'readInt32LE'); }
        readS16() { return read(this, 2, 'readInt16LE'); }
        readU8() { return read(this, 1, 'readUInt8'); }
    }
    const ptr = v => new Pointer(v);
    signatures.forEach(([address, hex]) => Buffer.from(hex, 'hex').copy(memory, address));
    if (options.mismatch) memory[0x55aa30] = 0;
    [0x76bdec, 0x76bde0, 0x76bedc, 0x76bed4, 0x76bf1c, 0x76c5d8].forEach((address, i) => memory.writeUInt32LE(0x800000 + i * 0x100, address));
    const context = {
        Process: { arch: 'ia32', platform: 'windows', pointerSize: 4,
            mainModule: { base: ptr(0x400000), path: 'fixture.exe' }, enumerateModules: () => [] },
        ptr, send: e => events.push(JSON.parse(JSON.stringify(e))),
        Interceptor: { attach: (p, callbacks) => {
            if (options.failInstallAt === hooks.size) throw new Error('fixture install failed');
            hooks.set(p.value, callbacks);
            return { detach: () => detached.push(p.value) };
        } },
        setInterval: fn => { timer = fn; return 1; }, clearInterval: () => { timer = null; }, rpc: { exports: {} }
    };
    function run() { vm.runInNewContext(source, context, { filename: 'observe-rendering.js' }); }
    function enter(address, values, self = 0x10000, caller = 0x401000) {
        const invocation = { context: { ecx: ptr(self) }, returnAddress: ptr(caller) };
        hooks.get(address).onEnter.call(invocation, values.map(ptr)); return invocation;
    }
    function putPoint(address, x, y) { memory.writeInt32LE(x, address); memory.writeInt32LE(y, address + 4); }
    return { run, events, hooks, detached, memory, context, enter, putPoint, ptr, summary: () => timer() };
}
const f = fixture(); f.run();
assert.equal(f.hooks.size, 18);
assert.equal(f.events.at(-1).kind, 'ready');
f.putPoint(0x10024, 10, 58); f.putPoint(0x10034, 576, 512); f.putPoint(0x10064, 128, 192);
f.putPoint(0x20000, 0, 0); f.putPoint(0x20008, 576, 512);
const draw = f.enter(0x55d070, [0x20000]);
assert.deepEqual(f.events.at(-1).map.camera, { x: 128, y: 192 });
assert.deepEqual(f.events.at(-1).rectangle, { left: 0, top: 0, right: 576, bottom: 512 });
f.hooks.get(0x55d070).onLeave.call(draw);
f.putPoint(0x20100, 23, 47);
const lookup = f.enter(0x55a5a0, [0x20100, 0x20200, 0x20202, 0x20204]);
f.memory.writeInt16LE(5, 0x20200); f.memory.writeInt16LE(7, 0x20202); f.memory.writeInt16LE(4, 0x20204);
f.hooks.get(0x55a5a0).onLeave.call(lookup);
assert.deepEqual(f.events.at(-1).local, { x: 23, y: 47 });
assert.equal(f.events.at(-1).column, 5); assert.equal(f.events.at(-1).row, 7); assert.equal(f.events.at(-1).subregion, 4);
f.enter(0x4aaa40, [0x30000, 0x30004, 0x20000, 0x20000], 0x10000, 0x55d88e);
assert.equal(f.events.at(-1).kind, 'map-final-copy');
for (let i = 0; i < 20; i++) f.enter(0x55d070, [0]);
assert.equal(f.events.filter(e => e.kind === 'map-draw').length, 4, 'samples capped while every call counted');
assert.equal(f.context.rpc.exports.snapshot().counts['map-draw'], 21);
f.summary(); assert.equal(f.events.at(-1).kind, 'summary');
f.hooks.get(0x55d3e3).onEnter.call({ context: { esi: f.ptr(0x10000), edx: f.ptr(110) } }, []);
assert.equal(f.events.at(-1).kind, 'cache-out-of-range');
assert.equal(f.events.at(-1).slot, 110);
f.context.rpc.exports.stop(); assert.equal(f.detached.length, 18);
f.context.rpc.exports.stop(); assert.equal(f.detached.length, 18, 'stop is idempotent');
const mismatch = fixture({ mismatch: true });
assert.throws(() => mismatch.run(), /Signature mismatch/); assert.equal(mismatch.hooks.size, 0);
assert.equal(mismatch.events.at(-1).kind, 'refused');
const partial = fixture({ failInstallAt: 7 });
assert.throws(() => partial.run(), /fixture install failed/); assert.equal(partial.detached.length, 7);
console.log('PASS: rendering probe contracts, signed tile outputs, sample limits, preflight refusal, partial-install rollback. Native ABI/gameplay not exercised.');
