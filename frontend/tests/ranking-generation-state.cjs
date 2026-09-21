const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const ts = require('typescript');
const calls = [];
const http = { post: (url, body) => ({ subscribe: handlers => calls.push({ url, body, handlers }) }) };
const output = ts.transpileModule(fs.readFileSync('src/app/app.component.ts', 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, experimentalDecorators: true }
}).outputText;
const context = { exports: {}, location: { hostname: 'localhost' }, Date, URLSearchParams, window: {},
  require: name => name === '@angular/core' ? { Component: () => target => target, inject: () => http } : {} };
vm.runInNewContext(output, context);
const app = new context.exports.AppComponent();
app.generationQuantity = 2; app.generateNumbers();
assert.equal(calls[0].url.endsWith('/predictions/generate'), true);
assert.equal(calls[0].body.prizeRange, 5);
const numbers = [
  { rank: 1, group: 25, milhar: '9999', selectionType: 'STATISTICAL' },
  { rank: 2, group: 1, milhar: '0001', selectionType: 'STATISTICAL' }
];
calls[0].handlers.next({ numbers, windowDays: 30 });
assert.equal(app.generation.numbers[0].milhar, '9999', 'new ranking must preserve score order instead of sorting by animal');
app.generateForAllBankTimes = true; app.generateNumbers();
assert.equal(calls[1].url.endsWith('/predictions/generate-bank-day'), true);
calls[1].handlers.next({ sourcePrediction: { numbers, windowDays: 60 }, totalBetAmount: 80, message: 'ok' });
assert.equal(app.generation.numbers[0].rank, 1);
assert.equal(app.generationWindowDays, 60);
console.log('Ranking generation state regression checks passed.');
