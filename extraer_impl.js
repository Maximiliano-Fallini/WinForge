const fs = require('fs');
const raw = fs.readFileSync('winutil_tweaks.json', 'utf-8');

// Extraer cada bloque de tweak: desde "WPFTweaks..." hasta el siguiente
const tweakBlocks = raw.split(/(?="WPFTweaks\w+")/);

const results = [];

for (const block of tweakBlocks) {
    // Extraer nombre de la clave
    const keyMatch = block.match(/^"(\w+)"/);
    if (!keyMatch) continue;
    const key = keyMatch[1];

    // Extraer Content
    const contentMatch = block.match(/"Content"\s*:\s*"((?:[^"\\]|\\.)*)"/);
    const content = contentMatch ? contentMatch[1] : '?';

    // Extraer category
    const catMatch = block.match(/"category"\s*:\s*"([^"]*)"/);
    const cat = catMatch ? catMatch[1] : '';

    // Solo Essential y Advanced
    if (cat !== 'Essential Tweaks' && cat !== 'z__Advanced Tweaks - CAUTION') continue;

    // Extraer entradas de registry
    const regEntries = [];
    const regRe = /"Path"\s*:\s*"([^"]*)"[^}]*?"Name"\s*:\s*"([^"]*)"[^}]*?"Value"\s*:\s*"([^"]*)"[^}]*?"Type"\s*:\s*"([^"]*)"/g;
    let m;
    while ((m = regRe.exec(block)) !== null) {
        regEntries.push({ path: m[1], name: m[2], value: m[3], type: m[4] });
    }

    // Extraer scripts (InvokeScript)
    const scriptMatch = block.match(/"InvokeScript"\s*:\s*\[\s*"([\s\S]*?)"\s*\]/);
    const script = scriptMatch ? scriptMatch[1].replace(/\\n/g, '\n').replace(/\\r/g, '').replace(/\\"/g, '"') : '';

    // Extraer UndoScript
    const undoMatch = block.match(/"UndoScript"\s*:\s*\[\s*"([\s\S]*?)"\s*\]/);
    const undoScript = undoMatch ? undoMatch[1].replace(/\\n/g, '\n').replace(/\\r/g, '').replace(/\\"/g, '"') : '';

    // Extraer appx packages
    const appxMatch = block.match(/"appx"\s*:\s*\[\s*"([^"]*)"\s*\]/);
    const appx = appxMatch ? appxMatch[1] : '';

    // Extraer service
    const svcMatch = block.match(/"Service"\s*:\s*\[\s*"([^"]*)"\s*\]/);
    const service = svcMatch ? svcMatch[1] : '';

    results.push({
        key,
        content,
        category: cat,
        regEntries,
        script: script.slice(0, 500),
        undoScript: undoScript.slice(0, 500),
        appx,
        service
    });
}

// Mostrar resultados
for (const r of results) {
    console.log(`=== ${r.key} (${r.category}) ===`);
    console.log(`  Content: ${r.content}`);
    if (r.regEntries.length > 0) {
        console.log(`  Registry:`);
        for (const e of r.regEntries) {
            console.log(`    ${e.path} | ${e.name} = ${e.value} (${e.type})`);
        }
    }
    if (r.script) console.log(`  Script: ${r.script.slice(0, 200)}...`);
    if (r.undoScript) console.log(`  Undo: ${r.undoScript.slice(0, 200)}...`);
    if (r.appx) console.log(`  Appx: ${r.appx}`);
    if (r.service) console.log(`  Service: ${r.service}`);
    console.log('');
}