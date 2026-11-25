(function (input) {
    var interaction = app.userInteractionLevel;
    var coordinates = app.coordinateSystem;
    var document, importedDocument, reopened;
    var phase = 'open SVG';
    try {
        app.userInteractionLevel = UserInteractionLevel.DONTDISPLAYALERTS;
        app.coordinateSystem = CoordinateSystem.DOCUMENTCOORDINATESYSTEM;
        importedDocument = openExclusive(input.source);
        phase = 'create Illustrator document';
        document = input.largeCanvas ? largeDocument() : importedDocument;
        var imported = importedDocument.layers[0];
        var emptyLayer = input.largeCanvas ? document.layers[0] : null;
        if (importedDocument.layers.length !== 1) throw Error('Unexpected SVG import layer structure.');
        var placed = [];
        for (var c = 0; c < input.categories.length; c++) {
            var category = input.categories[c];
            phase = 'prepare layer ' + category.label;
            var originalGroup = groupNamed(imported, category.id);
            var categoryLayer = document.layers.add();
            categoryLayer.name = category.label;
            for (var l = 0; l < category.layers.length; l++) {
                var expected = category.layers[l];
                var group = groupNamed(originalGroup, expected.id);
                var layer = categoryLayer.layers.add();
                layer.name = expected.label;
                if (input.largeCanvas) {
                    phase = 'copy imported group ' + expected.label;
                    var copy = group.duplicate(layer, ElementPlacement.PLACEATEND);
                    placed.push(copy);
                    group = copy;
                } else {
                    group.move(layer, ElementPlacement.PLACEATEND);
                }
                if (expected.roundCaps > 0 && expected.roundCaps === expected.lines)
                    roundStrokes(group, expected.dashed === expected.lines, document.scaleFactor || 1);
            }
            if (!input.largeCanvas) {
                if (originalGroup.pageItems.length !== 0) throw Error('Unassigned SVG group: ' + category.id);
                originalGroup.remove();
            }
        }
        phase = 'finish layer transfer';
        if (input.largeCanvas) {
            emptyLayer.remove();
            importedDocument.close(SaveOptions.DONOTSAVECHANGES);
            alignToArtboard(document, placed);
        } else {
            if (imported.pageItems.length !== 0) throw Error('Unassigned SVG paths remain.');
            imported.remove();
        }
        importedDocument = null;
        phase = 'verify actual paper size before saving';
        checkSize(document);
        var options = new IllustratorSaveOptions();
        options.pdfCompatible = true;
        options.compressed = true;
        options.embedICCProfile = true;
        phase = 'save layered AI';
        document.saveAs(File(input.staging), options);
        document.close(SaveOptions.DONOTSAVECHANGES);
        document = null;
        phase = 'reopen and verify layered AI';
        reopened = openExclusive(input.staging);
        checkSize(reopened);
        return verifyLayers(reopened);
    } catch (error) {
        throw Error(phase + ': ' + error + ' (line ' + error.line + ')');
    } finally {
        if (reopened) reopened.close(SaveOptions.DONOTSAVECHANGES);
        if (document && document !== importedDocument) document.close(SaveOptions.DONOTSAVECHANGES);
        if (importedDocument) importedDocument.close(SaveOptions.DONOTSAVECHANGES);
        app.coordinateSystem = coordinates;
        app.userInteractionLevel = interaction;
    }

    function alignToArtboard(doc, groups) {
        var board = doc.artboards[0].artboardRect;
        var drawing = doc.geometricBounds;
        var margin = .075 * 72 / 25.4 / doc.scaleFactor;
        var x = board[0] + margin - drawing[0];
        var y = board[1] - margin - drawing[1];
        for (var i = 0; i < groups.length; i++) groups[i].translate(x, y);
    }

    function largeDocument() {
        var preset = new DocumentPreset();
        preset.width = input.width * 72 / 25.4;
        preset.height = input.height * 72 / 25.4;
        preset.numArtboards = 1;
        preset.units = RulerUnits.Millimeters;
        preset.colorMode = DocumentColorSpace.RGB;
        var large = app.documents.addDocument('Print', preset, false);
        if (large.scaleFactor !== 10) {
            large.close(SaveOptions.DONOTSAVECHANGES);
            throw Error('Illustrator did not create the requested large canvas.');
        }
        return large;
    }

    function openExclusive(path) {
        var file = File(path);
        for (var i = 0; i < app.documents.length; i++) {
            var other;
            try { other = app.documents[i].fullName; } catch (_) { continue; }
            if (other && other.fsName.toLowerCase() === file.fsName.toLowerCase())
                throw Error('The file is already open: ' + path);
        }
        return app.open(file);
    }

    function groupNamed(parent, name) {
        for (var i = 0; i < parent.groupItems.length; i++)
            if (parent.groupItems[i].name === name && parent.groupItems[i].parent === parent)
                return parent.groupItems[i];
        throw Error('Missing imported group: ' + name);
    }

    function layerNamed(parent, name) {
        for (var i = 0; i < parent.layers.length; i++)
            if (parent.layers[i].name === name) return parent.layers[i];
        throw Error('Missing saved layer: ' + name);
    }

    function checkSize(doc) {
        var box = doc.artboards[0].artboardRect;
        var factor = doc.scaleFactor || 1;
        var millimeters = 25.4 / 72 * factor;
        var paperWidth = (box[2] - box[0]) * millimeters;
        var paperHeight = (box[1] - box[3]) * millimeters;
        if (doc.artboards.length !== 1 || Math.abs(paperWidth - input.width) > .002 ||
            Math.abs(paperHeight - input.height) > .002)
            throw Error('Illustrator paper is ' + paperWidth + ' x ' + paperHeight + ' mm (canvas scale ' + factor +
                '), expected ' + input.width + ' x ' + input.height + ' mm.');
        var drawing = doc.geometricBounds;
        // The SVG writer reserves half the 0.15 mm stroke width on each edge.
        var width = (drawing[2] - drawing[0]) * millimeters;
        var height = (drawing[1] - drawing[3]) * millimeters;
        var left = (drawing[0] - box[0]) * millimeters;
        var top = (box[1] - drawing[1]) * millimeters;
        if (!isFinite(width) || !isFinite(height) || Math.abs(width - (input.width - .15)) > input.drawingTolerance ||
            Math.abs(height - (input.height - .15)) > input.drawingTolerance ||
            Math.abs(left - .075) > input.drawingTolerance || Math.abs(top - .075) > input.drawingTolerance)
            throw Error('Illustrator changed the drawing scale or position within the artboard. Drawing ' +
                width + ' x ' + height + ' mm, inset ' + left + ' x ' + top + ' mm.');
    }

    function verifyLayers(doc) {
        var millimeters = 25.4 / 72 * (doc.scaleFactor || 1);
        var total = {lines: 0, closedLines: 0, roundCaps: 0, dashed: 0, regions: 0, loops: 0, layers: 0};
        if (doc.layers.length !== input.categories.length) throw Error('The saved category layer count changed.');
        for (var c = 0; c < input.categories.length; c++) {
            var expectedCategory = input.categories[c];
            var category = layerNamed(doc, expectedCategory.label);
            if (category.layers.length !== expectedCategory.layers.length) throw Error('Original layers changed.');
            for (var l = 0; l < expectedCategory.layers.length; l++) {
                var expected = expectedCategory.layers[l];
                var layer = layerNamed(category, expected.label);
                var group = groupNamed(layer, expected.id);
                var actual = {lines: 0, closedLines: 0, roundCaps: 0, dashed: 0, regions: 0, loops: 0};
                readItems(group, actual, millimeters);
                if (actual.lines !== expected.lines || actual.regions !== expected.regions ||
                    actual.loops !== expected.loops || actual.closedLines !== expected.closedLines ||
                    actual.roundCaps !== expected.roundCaps || actual.dashed !== expected.dashed)
                    throw Error('Saved path topology changed in ' + expected.label);
                total.lines += actual.lines;
                total.closedLines += actual.closedLines;
                total.roundCaps += actual.roundCaps;
                total.dashed += actual.dashed;
                total.regions += actual.regions;
                total.loops += actual.loops;
                total.layers++;
            }
        }
        if (doc.pathItems.length !== total.lines + total.loops)
            throw Error('Unexpected paths in the saved Illustrator document.');
        return 'PASS Illustrator reopen. ' + total.lines + ' editable lines, ' + total.regions +
            ' filled regions, ' + total.loops + ' closed fill loops, ' + total.layers +
            ' original layer entries. ' + total.closedLines + ' closed line frames, ' +
            total.roundCaps + ' strokes with round caps and joins, ' + total.dashed + ' dashed strokes. Paper ' +
            input.width + ' x ' + input.height + ' mm.';
    }

    function roundStrokes(parent, dashed, factor) {
        for (var i = 0; i < parent.pageItems.length; i++) {
            var item = parent.pageItems[i];
            if (item.parent !== parent) continue;
            if (item.typename === 'GroupItem') roundStrokes(item, dashed, factor);
            else if (item.typename === 'PathItem' && item.stroked && !item.filled) {
                item.strokeCap = StrokeCap.ROUNDENDCAP;
                item.strokeJoin = StrokeJoin.ROUNDENDJOIN;
                if (dashed) item.strokeDashes = [0.7 * 72 / 25.4 / factor, 0.4 * 72 / 25.4 / factor];
            }
        }
    }

    function readItems(parent, result, millimeters) {
        for (var i = 0; i < parent.pageItems.length; i++) {
            var item = parent.pageItems[i];
            if (item.parent !== parent) continue;
            if (item.typename === 'GroupItem') {
                readItems(item, result, millimeters);
            } else if (item.typename === 'CompoundPathItem') {
                result.regions++;
                for (var j = 0; j < item.pathItems.length; j++) {
                    if (!item.pathItems[j].closed) throw Error('Open compound fill.');
                    result.loops++;
                }
            } else if (item.typename === 'PathItem' && item.filled) {
                if (!item.closed) throw Error('Open filled path.');
                result.regions++;
                result.loops++;
            } else if (item.typename === 'PathItem' && item.stroked) {
                if (Math.abs(item.strokeWidth * millimeters - .15) > .002)
                    throw Error('Illustrator changed the 0.15 mm stroke width.');
                result.lines++;
                if (item.closed) result.closedLines++;
                var pattern = item.strokeDashes;
                if (pattern && Number(pattern[0]) > 0 && Number(pattern[1]) > 0) result.dashed++;
                if (item.strokeCap === StrokeCap.ROUNDENDCAP && item.strokeJoin === StrokeJoin.ROUNDENDJOIN)
                    result.roundCaps++;
            } else {
                throw Error('Unexpected page item in the vector drawing.');
            }
        }
    }
})(BETTERMAKE2D_INPUT);
