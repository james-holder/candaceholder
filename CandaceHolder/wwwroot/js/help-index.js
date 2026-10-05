// Page logic for Views/Help/Index.cshtml — FAQ accordion toggle. Extracted 2026-07-17
// from an inline <script> block. No Razor dependencies — pure JS, safe as a static file.

function toggleFaq(btn) {
    const answer = btn.nextElementSibling;
    const icon   = btn.querySelector('i');
    const isOpen = answer.classList.contains('open');
    // close all
    document.querySelectorAll('.faq-answer').forEach(a => a.classList.remove('open'));
    document.querySelectorAll('.faq-item button i').forEach(i => i.style.transform = '');
    if (!isOpen) {
        answer.classList.add('open');
        icon.style.transform = 'rotate(180deg)';
    }
}
