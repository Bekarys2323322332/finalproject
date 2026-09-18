// Editing of attribute values, shared by the profile page and the CV page.
//
// What it does:
//  * tracks which fields the user changed, locally, without touching the server
//    on every keystroke;
//  * flushes the changed ones every 5 seconds (the spec asks for 5-10);
//  * sends the version it holds for each value and replaces it with the version
//    the server returns;
//  * when the server reports a conflict, it puts the winning value into the
//    field and tells the user, instead of overwriting somebody else's edit.
//
// Markup contract - one container per value:
//   <div data-value-field data-attribute-id="12" data-type="Numeric" data-version="3">
//       <input ...>                      (or two inputs with data-role start/end)
//   </div>
(function () {
    const containers = Array.prototype.slice.call(document.querySelectorAll('[data-value-field]'));
    if (containers.length === 0) {
        return;
    }

    const form = document.getElementById('valueEditor');
    const token = form ? form.querySelector('input[name="__RequestVerificationToken"]').value : null;
    const statusBox = document.getElementById('saveStatus');
    const ownerId = form ? form.getAttribute('data-owner-id') : null;
    const saveUrl = form ? form.getAttribute('data-save-url') : '/values/save';

    // Attribute ids waiting to be sent.
    const dirty = new Set();
    let saving = false;

    function setStatus(text, cssClass) {
        if (!statusBox) return;
        statusBox.textContent = text;
        statusBox.className = 'small ' + (cssClass || 'text-body-secondary');
    }

    function read(container) {
        const type = container.getAttribute('data-type');
        const item = {
            attributeId: parseInt(container.getAttribute('data-attribute-id'), 10),
            version: parseInt(container.getAttribute('data-version'), 10),
            hasValue: false
        };

        if (type === 'Boolean') {
            const box = container.querySelector('input[type=checkbox]');
            item.valueBoolean = box.checked;
            // A checkbox is always an answer once the user has touched it.
            item.hasValue = true;
            return item;
        }

        if (type === 'Numeric') {
            const input = container.querySelector('input');
            const parsed = parseFloat(input.value);
            item.valueNumber = isNaN(parsed) ? null : parsed;
            item.hasValue = item.valueNumber !== null;
            return item;
        }

        if (type === 'Date') {
            const input = container.querySelector('input');
            item.valueDate = input.value || null;
            item.hasValue = !!input.value;
            return item;
        }

        if (type === 'Period') {
            const start = container.querySelector('[data-role=start]');
            const end = container.querySelector('[data-role=end]');
            item.valueDate = start.value || null;
            item.valueDateEnd = end.value || null;
            item.hasValue = !!start.value;
            return item;
        }

        if (type === 'Dropdown') {
            const select = container.querySelector('select');
            item.valueOptionId = select.value ? parseInt(select.value, 10) : null;
            item.hasValue = item.valueOptionId !== null;
            return item;
        }

        // String, Text and Image all live in the string column; for Image the
        // value is the URL the upload widget put there.
        const field = container.querySelector('input, textarea');
        item.valueString = field.value || null;
        item.hasValue = !!(field.value && field.value.trim().length > 0);
        return item;
    }

    function write(container, result) {
        const type = container.getAttribute('data-type');
        container.setAttribute('data-version', result.version);

        if (type === 'Boolean') {
            container.querySelector('input[type=checkbox]').checked = !!result.valueBoolean;
        } else if (type === 'Numeric') {
            container.querySelector('input').value = result.valueNumber ?? '';
        } else if (type === 'Date') {
            container.querySelector('input').value = result.valueDate ?? '';
        } else if (type === 'Period') {
            container.querySelector('[data-role=start]').value = result.valueDate ?? '';
            container.querySelector('[data-role=end]').value = result.valueDateEnd ?? '';
        } else if (type === 'Dropdown') {
            container.querySelector('select').value = result.valueOptionId ?? '';
        } else {
            container.querySelector('input, textarea').value = result.valueString ?? '';
        }

        markEmptyState(container, result.hasValue);
    }

    // Empty values are highlighted, which is what both the candidate and the
    // recruiter see as "missing".
    function markEmptyState(container, hasValue) {
        container.classList.toggle('value-missing', !hasValue);
    }

    function flush() {
        if (saving || dirty.size === 0) {
            return;
        }

        const batch = containers.filter(function (c) {
            return dirty.has(parseInt(c.getAttribute('data-attribute-id'), 10));
        });

        const payload = { userId: ownerId, items: batch.map(read) };

        // Clear the queue now; anything the user types while the request is in
        // flight marks itself dirty again and goes in the next round.
        dirty.clear();
        saving = true;
        setStatus('Saving…');

        fetch(saveUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify(payload)
        })
            .then(function (response) {
                if (!response.ok) throw new Error('save failed');
                return response.json();
            })
            .then(function (data) {
                let conflicts = 0;

                data.results.forEach(function (result) {
                    const container = containers.find(function (c) {
                        return parseInt(c.getAttribute('data-attribute-id'), 10) === result.attributeId;
                    });
                    if (!container) return;

                    if (result.conflict) {
                        conflicts++;
                        // Somebody else won. Show their value rather than
                        // silently re-applying ours.
                        write(container, result);
                        container.classList.add('value-conflict');
                    } else {
                        container.setAttribute('data-version', result.version);
                        container.classList.remove('value-conflict');
                        markEmptyState(container, result.hasValue);
                    }
                });

                setStatus(
                    conflicts > 0
                        ? conflicts + ' field(s) were changed by someone else and have been reloaded'
                        : 'Saved',
                    conflicts > 0 ? 'small text-danger' : 'small text-success');
            })
            .catch(function () {
                // Put the batch back so the next tick retries it.
                batch.forEach(function (c) {
                    dirty.add(parseInt(c.getAttribute('data-attribute-id'), 10));
                });
                setStatus('Could not save, retrying…', 'small text-danger');
            })
            .then(function () {
                saving = false;
            });
    }

    containers.forEach(function (container) {
        const id = parseInt(container.getAttribute('data-attribute-id'), 10);

        container.addEventListener('input', function () { dirty.add(id); setStatus('Unsaved changes…'); });
        container.addEventListener('change', function () { dirty.add(id); setStatus('Unsaved changes…'); });
    });

    setInterval(flush, 5000);

    // Also flush when the page is being left, so the last few seconds of typing
    // are not lost.
    window.addEventListener('beforeunload', flush);

    // Exposed so the image uploader can mark its field dirty after an upload.
    window.valueEditor = { markDirty: function (attributeId) { dirty.add(attributeId); }, flush: flush };
})();
