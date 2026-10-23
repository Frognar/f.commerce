// Product gallery: thumbnails switch the main image; the main image opens a lightbox with keyboard navigation.
// Without JavaScript the main image is a link to the large version.
(() => {
    const init = (gallery) => {
        if (gallery.dataset.galleryReady) return;
        gallery.dataset.galleryReady = "true";

        const main = gallery.querySelector("[data-gallery-main]");
        const mainImage = main.querySelector("img");
        const thumbs = [...gallery.querySelectorAll("[data-gallery-thumb]")];
        const dialog = gallery.querySelector("[data-gallery-dialog]");
        const large = dialog.querySelector("[data-gallery-large]");
        const counter = dialog.querySelector("[data-gallery-counter]");
        const images = thumbs.length > 0
            ? thumbs.map((t) => ({ medium: t.dataset.medium, large: t.dataset.large }))
            : [{ medium: mainImage.src, large: main.href }];
        let current = 0;

        const select = (index) => {
            current = (index + images.length) % images.length;
            mainImage.src = images[current].medium;
            main.href = images[current].large;
            thumbs.forEach((t, i) => t.toggleAttribute("aria-current", i === current));
            large.src = images[current].large;
            counter.textContent = images.length > 1 ? `${current + 1} / ${images.length}` : "";
        };

        thumbs.forEach((thumb, index) => thumb.addEventListener("click", () => select(index)));
        main.addEventListener("click", (event) => {
            event.preventDefault();
            select(current);
            dialog.showModal();
        });
        dialog.querySelector("[data-gallery-close]").addEventListener("click", () => dialog.close());
        dialog.querySelector("[data-gallery-prev]")?.addEventListener("click", () => select(current - 1));
        dialog.querySelector("[data-gallery-next]")?.addEventListener("click", () => select(current + 1));
        dialog.addEventListener("click", (event) => { if (event.target === dialog) dialog.close(); });
        dialog.addEventListener("keydown", (event) => {
            if (event.key === "ArrowLeft") select(current - 1);
            if (event.key === "ArrowRight") select(current + 1);
        });
    };

    const scan = (root) => root.querySelectorAll("[data-gallery]").forEach(init);
    document.addEventListener("DOMContentLoaded", () => scan(document));
    document.addEventListener("htmx:load", (event) => scan(event.target));
})();
