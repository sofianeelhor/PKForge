namespace PKForge.Domain;

/// <summary>
/// National-dex classification sets for the Pokédex picker's category filters.
/// Every id was resolved through the pinned PKHeX name table (see SpeciesCategoriesTests,
/// which re-verifies the members by name) - never from memory of dex order.
/// </summary>
public static class SpeciesCategories
{
    public static readonly IReadOnlySet<int> Legendary = new HashSet<int>
    {
        // Gen 1-4
        144, 145, 146, 150, 243, 244, 245, 249, 250,
        377, 378, 379, 380, 381, 382, 383, 384,
        480, 481, 482, 483, 484, 485, 486, 487, 488,
        // Gen 5-7
        638, 639, 640, 641, 642, 643, 644, 645, 646,
        716, 717, 718, 772, 773, 785, 786, 787, 788, 789, 790, 791, 792, 800,
        // Gen 8-9
        888, 889, 890, 891, 892, 894, 895, 896, 897, 898, 905,
        1007, 1008, 1001, 1002, 1003, 1004, 1014, 1015, 1016, 1017, 1024,
    };

    public static readonly IReadOnlySet<int> Mythical = new HashSet<int>
    {
        151, 251, 385, 386, 489, 490, 491, 492, 493, 494,
        647, 648, 649, 719, 720, 721, 801, 802, 807, 808, 809, 893, 1025,
    };

    public static readonly IReadOnlySet<int> UltraBeast = new HashSet<int>
    {
        793, 794, 795, 796, 797, 798, 799, 803, 804, 805, 806,
    };

    public static readonly IReadOnlySet<int> Paradox = new HashSet<int>
    {
        984, 985, 986, 987, 988, 989, 990, 991, 992, 993, 994, 995,
        1005, 1006, 1009, 1010, 1020, 1021, 1022, 1023,
    };

    public static readonly IReadOnlySet<int> PseudoLegendary = new HashSet<int>
    {
        147, 148, 149, 246, 247, 248, 374, 375, 376, 443, 444, 445,
        633, 634, 635, 704, 705, 706, 782, 783, 784, 885, 886, 887, 996, 997, 998,
    };

    /// <summary>Complete starter lines, all nine generations.</summary>
    public static readonly IReadOnlySet<int> Starter = new HashSet<int>
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9,
        152, 153, 154, 155, 156, 157, 158, 159, 160,
        252, 253, 254, 255, 256, 257, 258, 259, 260,
        387, 388, 389, 390, 391, 392, 393, 394, 395,
        495, 496, 497, 498, 499, 500, 501, 502, 503,
        650, 651, 652, 653, 654, 655, 656, 657, 658,
        722, 723, 724, 725, 726, 727, 728, 729, 730,
        810, 811, 812, 813, 814, 815, 816, 817, 818,
        906, 907, 908, 909, 910, 911, 912, 913, 914,
    };

    public static readonly IReadOnlySet<int> Fossil = new HashSet<int>
    {
        138, 139, 140, 141, 142, 345, 346, 347, 348, 408, 409, 410, 411,
        564, 565, 566, 567, 696, 697, 698, 699, 880, 881, 882, 883,
    };

    public static readonly IReadOnlySet<int> Baby = new HashSet<int>
    {
        172, 173, 174, 175, 236, 238, 239, 240, 298, 360,
        406, 433, 438, 439, 440, 446, 447, 458,
    };

    /// <summary>The complete, closed G-Max roster (Sword/Shield era, never extended).</summary>
    public static readonly IReadOnlySet<int> GigantamaxCapable = new HashSet<int>
    {
        3, 6, 9, 12, 25, 52, 68, 94, 99, 131, 133, 143,
        569, 700, 809, 812, 815, 818, 823, 825, 834, 839, 841, 842,
        844, 851, 858, 861, 869, 879, 884, 892,
    };

    /// <summary>Theme sets: curated and intentionally subjective, the fun kind of filter.</summary>
    public static class Themes
    {
        public static readonly IReadOnlySet<int> Felines = new HashSet<int>
        {
            52, 53, 300, 301, 403, 404, 405, 677, 678, 667, 668,
            725, 726, 727, 807, 906, 907, 908,
        };

        public static readonly IReadOnlySet<int> Canines = new HashSet<int>
        {
            37, 38, 58, 59, 133, 134, 209, 210, 228, 229, 261, 262,
            309, 310, 447, 448, 506, 507, 508, 570, 571, 744, 745,
            835, 836, 926, 927, 942, 943,
        };

        public static readonly IReadOnlySet<int> Birds = new HashSet<int>
    {
            16, 17, 18, 21, 22, 23, 84, 85, 163, 164, 177, 178, 225,
            333, 334, 393, 394, 395, 441, 561, 580, 581, 627, 628,
            661, 662, 663, 731, 732, 733, 821, 822, 823, 964, 973, 940, 941,
        };

        public static readonly IReadOnlySet<int> Dinosaurs = new HashSet<int>
        {
            131, 142, 246, 247, 248, 304, 305, 306, 371, 372, 373,
            408, 409, 410, 411, 566, 567, 696, 697, 698, 699, 846, 847, 848, 996, 997, 998,
        };

        /// <summary>Dragon-adjacent mons that carry no Dragon typing (the type itself is matched separately).</summary>
        public static readonly IReadOnlySet<int> DragonExtras = new HashSet<int> { 6, 130, 334, 260, 973 };

        /// <summary>Sea-dwellers without the Water typing (Water itself is matched separately).</summary>
        public static readonly IReadOnlySet<int> AquaticExtras = new HashSet<int> { 346, 604, 618, 781 };
    }
}
