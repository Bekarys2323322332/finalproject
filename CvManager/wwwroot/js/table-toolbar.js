// Shared behaviour for every table in the app. The spec forbids per-row
// buttons, so each table has one toolbar on top that acts on the checked rows:
// this script keeps the select-all box in sync and enables or disables the
// toolbar buttons depending on what is selected.
//
// Markup contract:
//   <form data-table-form>            the form the toolbar buttons submit
//     <button data-requires="one">    enabled only when exactly one row is checked
//     <button data-requires="any">    enabled when at least one row is checked
//     <input type="checkbox" data-select-all>
//     <input type="checkbox" name="ids" class="row-select">
(function () {
    document.querySelectorAll('[data-table-form]').forEach(function (form) {
        const selectAll = form.querySelector('[data-select-all]');
        const rows = Array.prototype.slice.call(form.querySelectorAll('.row-select'));
        const buttons = Array.prototype.slice.call(form.querySelectorAll('[data-requires]'));

        function update() {
            const checked = rows.filter(function (r) { return r.checked; });

            buttons.forEach(function (button) {
                const mode = button.getAttribute('data-requires');
                if (mode === 'one') {
                    button.disabled = checked.length !== 1;
                } else {
                    button.disabled = checked.length === 0;
                }
            });

            if (selectAll) {
                selectAll.checked = rows.length > 0 && checked.length === rows.length;
                // Partial selection shows the dash instead of a tick.
                selectAll.indeterminate = checked.length > 0 && checked.length < rows.length;
            }
        }

        if (selectAll) {
            selectAll.addEventListener('change', function () {
                rows.forEach(function (r) { r.checked = selectAll.checked; });
                update();
            });
        }

        rows.forEach(function (row) {
            row.addEventListener('change', update);
        });

        // Clicking anywhere on the row toggles its checkbox, so the tiny box is
        // not the only target. Links and the checkbox itself are left alone.
        form.querySelectorAll('tr[data-row]').forEach(function (tr) {
            tr.addEventListener('click', function (event) {
                if (event.target.closest('a') || event.target.classList.contains('row-select')) {
                    return;
                }
                const box = tr.querySelector('.row-select');
                if (box) {
                    box.checked = !box.checked;
                    update();
                }
            });
        });

        update();
    });
})();
