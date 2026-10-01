# Discord announcements

Release announcements are sent automatically after a tagged release has passed
the image and package publication steps. The ChaptarrNG repository must have an
Actions secret named DISCORD_RELEASE_WEBHOOK with the same webhook value used
by SeerrNG; the webhook determines the channel.

Set the secret under GitHub repository Settings > Secrets and variables >
Actions. Never commit a webhook URL.

For one-off project announcements, run the Discord announcement workflow from
main and provide a title, HTTPS link, and message. The workflow uses the same
webhook and formats the post as a ChaptarrNG embed.

