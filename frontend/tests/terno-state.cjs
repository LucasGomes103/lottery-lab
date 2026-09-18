const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const ts = require('typescript');

const calls = [];
const http = Object.fromEntries(['get', 'post'].map(method => [method, (url, body) => ({
  subscribe: handlers => calls.push({ method, url, body, handlers })
})]));
const output = ts.transpileModule(fs.readFileSync('src/app/app.component.ts', 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, experimentalDecorators: true }
}).outputText;
class TestDate extends Date {
  constructor(...args) { super(...(args.length ? args : [2026, 8, 18, 13, 0])); }
}
const context = {
  exports: {}, location: { hostname: 'localhost' }, Date: TestDate, URLSearchParams,
  window: { confirm: () => true },
  require: name => name === '@angular/core' ? { Component: () => target => target, inject: () => http } : {}
};
vm.runInNewContext(output, context);
const app = new context.exports.AppComponent();
app.currentUser = { role: 'ADMIN' };
app.bank = 'LT NACIONAL'; app.time = '10:00'; app.generationDate = '2026-09-20';
app.generationWindowDays = 90; app.selectedAnimalGroups.add(2);
app.onTernoBankChange('LOOK LOTERIAS');
assert.equal(app.ternoTime, '14:00');
assert.equal(app.ternoDate, '2026-09-18');
assert.equal(app.bank, 'LT NACIONAL');
assert.equal(app.time, '10:00');
assert.equal(app.generationDate, '2026-09-20');
app.toggleTernoAnimalGroup(4, true);
app.ternoWindowDays = 30;
app.ternoQuantity = 4;
app.generateTernos();
const generated = calls.at(-1);
assert.equal(generated.body.bank, 'LOOK LOTERIAS');
assert.equal(generated.body.time, '14:00');
assert.equal(generated.body.windowDays, 30);
assert.equal(generated.body.groups.join(','), '4');
assert.equal(app.selectedAnimalGroups.has(2), true);
app.onGenerationBankChange('LT NACIONAL');
assert.equal(app.ternoTime, '14:00');
assert.equal(app.ternoBank, 'LOOK LOTERIAS');
assert.equal(app.ternoWindowDays, 30);
assert.equal(app.selectedTernoAnimalGroups.has(4), true);
app.ternoHistoryBank = 'LOOK LOTERIAS';
app.loadTernoHistory();
assert.equal(new URL(calls.at(-1).url).searchParams.get('bank'), 'LOOK LOTERIAS');
app.ternoHistory = [{ id: 'a' }, { id: 'b' }];
app.toggleTernoPageSelection();
assert.equal(app.selectedTernoIds.size, 2);
app.selectedTerno = { id: 'a' }; app.ternoGeneration = { id: 'a' };
app.deleteTernos(['a']);
const deletion = calls.at(-1);
assert.ok(deletion.url.endsWith('/ternos/delete-batch'));
assert.equal(deletion.body.ids.join(','), 'a');
deletion.handlers.next({ message: 'Excluído' });
assert.equal(app.selectedTerno, null);
assert.equal(app.ternoGeneration, null);
assert.equal(app.deletingTernos, false);
app.ternoGeneration = { id: 'saved', status: 'PENDING' };
app.checkTernoInHistory('saved');
assert.ok(calls.at(-1).url.endsWith('/ternos/saved'));
calls.at(-1).handlers.next({ id: 'saved', status: 'EVALUATED', hits: 1, returnAmount: 13000 });
assert.equal(app.ternoGeneration.status, 'EVALUATED');
assert.equal(app.ternoGeneration.hits, 1);
assert.equal(app.checkingTerno, false);
console.log('Terno state regression checks passed.');
