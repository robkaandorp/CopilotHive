// Scroll a Blazor ElementReference container to its bottom
function scrollToBottom(element) {
    if (element && element.scrollHeight !== undefined) {
        element.scrollTop = element.scrollHeight;
    }
}
