var fs = require('fs');
var toc = require('./toc.html.js');
var model = JSON.parse(fs.readFileSync("C:\\code\\docfx\\Documentation\\_site\\api\\toc.raw.json"));
var transformed = toc.transform(model);
fs.writeFileSync("output.json", JSON.stringify(transformed, null, "  "), "utf8");

