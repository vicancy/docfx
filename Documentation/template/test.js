var renderer = require('mustache');
var path = require('path');
if (process.argv.length < 4){
	console.log("Usage: node test.js [type](mref|md|rest) [inputModelPath] [option](transform|render|all) ");
	return 1;
}
var type = process.argv[2].toLowerCase();
var option = (process.argv[4] || 'all').toLowerCase();
var templateName = 'ManagedReference.html.primary';
if (type == 'md'){
	templateName = 'conceptual.html.primary';
}else if (type == 'rest'){
	templateName = 'RestAPI.html.primary';
}
var file = process.argv[3];

var fs = require('fs');
var filePath = file;

var content = fs.readFileSync(filePath, 'UTF8');
var inputModel = JSON.parse(fs.readFileSync(filePath, 'UTF8'));
var fileNameParts = path.basename(filePath).split('.');
var modelName = fileNameParts.slice(0, fileNameParts.length - 2).join('.');
var dir = path.dirname(filePath);
var outputModel;
if (option == 'transform' || option == 'all') {
	var prebuild = require('./' + templateName + '.js');
	outputModel = prebuild.transform(inputModel);
	
	var outputModelPath = dir+ '\\' + modelName + ".model.json";
	fs.writeFileSync(outputModelPath, JSON.stringify(outputModel));
	console.log("save transformed model to " + outputModelPath);
} else {
	outputModel = inputModel;
}
if (option == 'render' || option == 'all') {
	var templatePath = templateName + '.tmpl';
	var template = fs.readFileSync(templatePath, 'UTF8');

	var outputHtml = renderer.render(template, outputModel);
	var outputPath = dir+ '\\' + modelName + '.html';
	fs.writeFileSync(outputPath, outputHtml, 'UTF8');
	console.log("Successfully rendered to " + outputPath);
}